using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record SpeechSentence(string Id, int Number, string Text, string AudioRelativePath)
{
    public IReadOnlyList<string> ScoringCharacters => GetScoringCharacters(Text);

    public static string[] GetScoringCharacters(string text)
    {
        var result = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element) && !char.IsPunctuation(element, 0))
                result.Add(element);
        }
        return result.ToArray();
    }
}

public sealed record SpeechSentenceList(
    int Number,
    int Size,
    IReadOnlyList<SpeechSentence> Sentences
)
{
    public string Id => Number.ToString("00", CultureInfo.InvariantCulture);
    public int ResourceStart => Sentences[0].Number;
    public int ResourceEnd => Sentences[^1].Number;
}

public sealed record SpeechCorpus(
    string Id,
    IReadOnlyList<SpeechSentence> Sentences,
    AdaptiveScoringRule? AdaptiveScoring = null,
    string? AdaptiveConfigurationError = null,
    SpeechNoiseMapping? SnrNoise = null
);

public sealed record SpeechCorpusAudioResource(AudioBuffer Audio, string? FileSha256);

public interface ISpeechCorpusProvider
{
    IReadOnlyList<SpeechCorpus> Corpora { get; }
    AudioBuffer ReadAudio(SpeechSentence sentence);
    SpeechCorpusAudioResource ReadAudioResource(SpeechSentence sentence) =>
        new(ReadAudio(sentence), null);
}

/// <summary>Portable file-based corpus. No source checkout or UI runtime is required.</summary>
public sealed class FileSpeechCorpusProvider : ISpeechCorpusProvider
{
    private readonly string _root;

    public FileSpeechCorpusProvider(string? root = null)
    {
        _root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "SpeechCorpora"))
        );
        var directories = DiscoverDirectories(_root);
        if (directories.Length == 0)
            throw new InvalidDataException($"语料根目录没有语料库：{_root}");
        Corpora = directories
            .OrderBy(d =>
                Path.GetFileName(d) switch
                {
                    "MSP" => 0,
                    "MHINT" => 1,
                    "MHINT-P" => 2,
                    _ => 3,
                }
            )
            .ThenBy(Path.GetFileName, NaturalOrder.Instance)
            .Select(ReadCorpus)
            .ToArray();
    }

    private SpeechCorpus ReadCorpus(string directory)
    {
        var id = Path.GetFileName(directory);
        var sentences = ReadCorpusSentences(id, directory);
        var path = Path.Combine(directory, "corpus.json");
        SpeechNoiseMapping? noise = null;
        try
        {
            if (!Directory.GetFiles(directory).Any(p => Path.GetFileName(p) == "corpus.json"))
                throw new InvalidDataException("缺少 corpus.json 配置文件（文件名区分大小写）。");
            var json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            if (json.StartsWith('\uFEFF'))
                json = json[1..];
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("corpus.json 必须为对象。");
            noise = ReadNoiseMapping(root, path);
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("adaptiveScoring", out var scoring)
                || scoring.ValueKind != JsonValueKind.Object
            )
                throw new InvalidDataException("缺少 adaptiveScoring 评分规则对象。");
            int Integer(string name)
            {
                if (
                    !scoring.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt32(out var number)
                )
                    throw new InvalidDataException($"{name} 必须填写整数。");
                return number;
            }
            return new(
                id,
                sentences,
                new(Integer("expectedCharacterCount"), Integer("minimumCorrect")),
                SnrNoise: noise
            );
        }
        catch (Exception error)
            when (error
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or JsonException
                        or ArgumentException
            )
        {
            // Noise configuration remains usable independently of adaptive scoring.
            var message = $"语料库 {id} 的配置不可用（{path}）：{error.Message}";
            return new(
                id,
                sentences,
                AdaptiveConfigurationError: $"语料库 {id} 的自适应配置不可用（{path}）：{error.Message}",
                SnrNoise: noise ?? new(SsnError: message, BabbleError: message)
            );
        }
    }

    private static SpeechNoiseMapping ReadNoiseMapping(JsonElement root, string path)
    {
        (string? File, string? Error) Read(string key)
        {
            try
            {
                if (
                    !root.TryGetProperty("snrNoise", out var mapping)
                    || mapping.ValueKind != JsonValueKind.Object
                    || !mapping.TryGetProperty(key, out var value)
                    || value.ValueKind != JsonValueKind.String
                )
                    throw new InvalidDataException($"缺少 snrNoise.{key} 噪声配置。");
                var name = value.GetString()!;
                FileSpeechNoiseProvider.ValidateFileName(name);
                return (name, null);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException)
            {
                return (null, $"噪声配置不可用（{path}）：{e.Message}");
            }
        }
        var ssn = Read("ssn");
        var babble = Read("babble");
        return new(ssn.File, babble.File, ssn.Error, babble.Error);
    }

    private static string[] DiscoverDirectories(string root)
    {
        var directories = Directory.GetDirectories(root);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!names.Add(Path.GetFileName(directory)))
                throw new InvalidDataException($"存在仅大小写不同的重复标识：{directory}");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"语料目录不能是链接：{directory}");
        }
        return directories;
    }

    private IReadOnlyList<SpeechSentence> ReadCorpusSentences(string corpusId, string directory)
    {
        if (Directory.GetDirectories(directory).Length != 0)
            throw new InvalidDataException($"语料库必须平铺存储，不能包含句表子目录：{directory}");
        var files = Directory.GetFiles(directory);
        var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var textPath = Path.Combine(directory, "sentences.txt");
        if (!names.Contains("sentences.txt"))
            throw new FileNotFoundException($"语料库缺少 UTF-8 文字表：{textPath}", textPath);
        foreach (var file in files)
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"语料文件不能是链接：{file}");
        var texts = ReadSentences(textPath);
        var expected = Enumerable
            .Range(1, texts.Length)
            .Select(n => n.ToString(CultureInfo.InvariantCulture) + ".wav")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (
                Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                && !expected.Contains(name)
            )
                throw new InvalidDataException(
                    $"音频文件多余或命名错误：{file}；应按句序使用 1.wav 等名称。"
                );
            if (
                name.Equals("sentences.txt", StringComparison.OrdinalIgnoreCase)
                && name != "sentences.txt"
            )
                throw new InvalidDataException($"文字表文件名大小写错误：{file}");
        }
        var sentences = new List<SpeechSentence>();
        for (var i = 0; i < texts.Length; i++)
        {
            var fileName = (i + 1).ToString(CultureInfo.InvariantCulture) + ".wav";
            var path = Path.Combine(directory, fileName);
            if (!names.Contains(fileName))
                throw new FileNotFoundException($"语料音频缺失：{path}", path);
            var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
            sentences.Add(
                new(
                    $"{corpusId}/{(i + 1).ToString("000", CultureInfo.InvariantCulture)}",
                    i + 1,
                    texts[i],
                    relative
                )
            );
        }
        return sentences.ToArray();
    }

    private static string[] ReadSentences(string path)
    {
        string contents;
        try
        {
            contents = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException($"文字表不是有效的 UTF-8：{path}", error);
        }
        if (contents.StartsWith('\uFEFF'))
            contents = contents[1..];
        using var reader = new StringReader(contents);
        var texts = new List<string>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var text = line.Trim();
            if (text.Length == 0)
                continue;
            if (
                text.Any(char.IsControl)
                || new SpeechSentence("", 1, text, "").ScoringCharacters.Count == 0
            )
                throw new InvalidDataException($"文字表 {path} 第 {lineNumber} 行正文无效。");
            texts.Add(text);
        }
        if (texts.Count == 0)
            throw new InvalidDataException($"文字表没有句子：{path}");
        return texts.ToArray();
    }

    // Numeric segments compare by value without integer overflow; other segments use ordinal ordering.
    private sealed class NaturalOrder : IComparer<string?>
    {
        public static NaturalOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var left = Regex.Matches(x ?? "", @"[0-9]+|[^0-9]+");
            var right = Regex.Matches(y ?? "", @"[0-9]+|[^0-9]+");
            for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
            {
                var a = left[i].Value;
                var b = right[i].Value;
                var an = char.IsAsciiDigit(a[0]);
                var bn = char.IsAsciiDigit(b[0]);
                if (an != bn)
                    return an ? -1 : 1;
                int result;
                if (an)
                {
                    a = a.TrimStart('0');
                    b = b.TrimStart('0');
                    result = a.Length.CompareTo(b.Length);
                    if (result == 0)
                        result = StringComparer.Ordinal.Compare(a, b);
                }
                else
                    result = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (result != 0)
                    return result;
            }
            var count = left.Count.CompareTo(right.Count);
            return count != 0 ? count : StringComparer.Ordinal.Compare(x, y);
        }
    }

    public IReadOnlyList<SpeechCorpus> Corpora { get; }

    public AudioBuffer ReadAudio(SpeechSentence sentence) => ReadAudioResource(sentence).Audio;

    public SpeechCorpusAudioResource ReadAudioResource(SpeechSentence sentence)
    {
        var bytes = File.ReadAllBytes(Resolve(sentence.AudioRelativePath));
        using var stream = new MemoryStream(bytes, writable: false);
        return new(
            Pcm16WaveReader.Read(stream),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
        );
    }

    private string Resolve(string relative)
    {
        var path = Path.GetFullPath(
            Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))
        );
        var prefix = Path.EndsInDirectorySeparator(_root)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (
            !path.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
        )
            throw new InvalidDataException("语料路径超出资源目录。");
        return path;
    }
}
