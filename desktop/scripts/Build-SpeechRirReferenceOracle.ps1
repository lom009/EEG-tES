param(
    [Parameter(Mandatory)][string]$ResearchRoot,
    [Parameter(Mandatory)][string]$MatlabRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# Offline numerical reference only. Runtime never uses this GPL-derived artifact.
# Fallback for MATLAB installations whose MEX compiler detection is unavailable.
$ErrorActionPreference = 'Stop'
$source = Join-Path $ResearchRoot 'SpeechIntelligibility/RIR-Generator-master/rir_generator.cpp'
$lcc = Join-Path $MatlabRoot 'sys/lcc64/lcc64'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$code = Get-Content -LiteralPath $source -Raw
# Preserve the copyright header and all arithmetic; adapt only C++ allocation syntax.
$code = "#include <stdlib.h>`n#define false 0`n#define true 1`n" + $code
$code = [regex]::Replace($code, 'new (double|char)\[(.*)\]', '($1*)malloc(sizeof($1)*($2))')
$code = [regex]::Replace($code, 'delete\[\]\s*(\w+)\s*;', 'free($1);')
Set-Content -LiteralPath (Join-Path $output 'rir_generator.c') -Value $code -Encoding utf8
Set-Content -LiteralPath (Join-Path $output 'mexFunction.def') -Value "LIBRARY rir_generator.mexw64`nEXPORTS`nmexFunction" -Encoding ascii
Push-Location -LiteralPath $output
try {
    $compiler = Join-Path $lcc 'bin/lcc64.exe'
    $flags = @('-nodeclspec','-Zp8','-dll','-c',"-I$lcc/include64","-I$MatlabRoot/extern/include",'-DMATLAB_MEX_FILE','-noregistrylookup')
    & $compiler @flags rir_generator.c -o rir_generator.obj
    if ($LASTEXITCODE -ne 0) { throw 'Reference compilation failed.' }
    & $compiler @flags (Join-Path $lcc 'mex/lccstub.c') -o lccstub.obj
    if ($LASTEXITCODE -ne 0) { throw 'Reference stub compilation failed.' }
    & (Join-Path $lcc 'bin/lcclnk64.exe') -dll "-L$lcc/lib64" "-L$MatlabRoot/extern/lib/win64/microsoft" -entry LibMain lccstub.obj rir_generator.obj libmx.lib libmex.lib libmat.lib mexFunction.def -o rir_generator.mexw64
    if ($LASTEXITCODE -ne 0) { throw 'Reference linking failed.' }
} finally { Pop-Location }
Write-Output "Offline RIR oracle: $output"
