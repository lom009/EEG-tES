function GenerateSpeechVocoderReference(researchRoot, outputPath)
% Pure numerical fixtures only. Does not call the GUI, playback or device entrypoints.
% Requires MATLAB + Signal Processing Toolbox. Original research files remain untouched.
arguments
    researchRoot (1,:) char
    outputPath (1,:) char
end
source = fullfile(researchRoot,'SpeechIntelligibility','Vocoders');
working = tempname; mkdir(working);
oldpath = path; cleanup = onCleanup(@() restore(oldpath,working));
files = dir(fullfile(source,'**','*.m'));
for f = files'
    name = f.name;
    if ~ismember(name, {'ClassicalVoc.m','electrovocoder.m','Map_ACE.m','load_map.m', ...
            'check_map.m','timing_check.m','level_check.m','initialize_ACE.m','initialize_TLE.m','LGF_Q_diff.m','logarithmic_compression.m','ACE_Process.m','TLE_Process.m'})
        continue
    end
    text = fileread(fullfile(f.folder,name));
    if strcmp(name,'electrovocoder.m')
        first = strfind(text,'subplot(311)'); last = strfind(text,'function GauEnv');
        text = [text(1:first(1)-1), text(last(1):end)];
        text = strrep(text,'rand(1)','referenceUniform()');
        % Shape fix: MATLAB sum(row) collapses time when NumberOfBands == 1.
        text = strrep(text,'sum(ModulatedBands)','sum(ModulatedBands,1)');
    elseif strcmp(name,'ClassicalVoc.m')
        text = strrep(text,'randn(length(x),1)','referenceNormal(length(x))');
    elseif ismember(name,{'ACE_Process.m','TLE_Process.m'})
        text = regexprep(text,'(?m)^plot_electrodogram[^\r\n]*','');
    end
    fid=fopen(fullfile(working,name),'w'); fwrite(fid,text); fclose(fid);
end
% Helpers inject an identical, version-independent random realization in C# and MATLAB.
write(working,'referenceUniform.m', ["function v=referenceUniform()", "global refState refUniforms", ...
    "refState=mod(refState*1664525+1013904223,4294967296);", "v=(refState+.5)/4294967296; refUniforms(end+1)=v;", "end"]);
write(working,'referenceNormal.m', ["function v=referenceNormal(n)", "v=zeros(n,1);", ...
    "for k=1:n, v(k)=sqrt(-2*log(referenceUniform()))*cos(2*pi*referenceUniform()); end", "end"]);
addpath(working,'-begin');
cases={}; index=0;
[speech,speechRate]=audioread(fullfile(fileparts(fileparts(mfilename('fullpath'))),'EGGtCSPlatform.SpeechTraining','Resources','MHINT-P','1.wav'));
assert(speechRate==16000); speech=mean(speech,2);
[~,peak]=max(abs(speech)); start=max(1,min(length(speech)-639,peak-320)); speech=speech(start:start+639);
for signal=1:2
for bands=[1,8,22]
 for algorithm=0:3
  for carrier=0:1
    n=513; t=(0:n-1)'/16000;
    x=(.2+.1*sin(2*pi*23*t)).*(sin(2*pi*330*t)+.35*sin(2*pi*1730*t));
    if signal==2, x=speech; end
    x=round(x*32768)/32768;
    global refState refUniforms; refUniforms=[]; refState=double(bitxor(uint32(1729),uint32(hex2dec('9E3779B9'))));
    maxima=min(8,bands); if signal==2, maxima=min(3,bands); end; q=[];
    if mod(algorithm,2)==0
        y=ClassicalVoc(x,16000,bands,2-carrier,1+(algorithm==2));
    else
        paras.NumberOfBands=bands; paras.Nmaxima=maxima; paras.THR=100; paras.MCL=255;
        paras.BandGain=0; paras.StimulationRate=1000;
        p=load_map(paras);
        if algorithm==1, p=initialize_ACE(p); else, p=initialize_TLE(p); end
        p.Left.lr_select='left';
        if algorithm==1, [q,map]=ACE_Process(x,p.Left); else, [q,map]=TLE_Process(x,p.Left); end
        y=electrovocoder(q,map,2-carrier); y=y(:); y=y*rms(x)/rms(y);
    end
    index=index+1;
    c=struct('Algorithm',algorithm,'Carrier',carrier,'Bands',bands,'Maxima',maxima,'Seed',1729,'Input',x,'Output',y,'Uniforms',refUniforms,'Signal',signal);
    if ~isempty(q), c.Electrodes=q.electrodes; c.Levels=q.current_levels; c.Alpha=map.lgf_alpha; c.Weights=map.weights; end
    cases{index}=c;
  end
 end
end
end
fid=fopen(outputPath,'w'); assert(fid>=0); fwrite(fid,jsonencode(cases)); fclose(fid);
fprintf('Wrote %d research reference cases to %s\n',index,outputPath);
end
function write(folder,name,lines)
fid=fopen(fullfile(folder,name),'w');fprintf(fid,'%s\n',lines);fclose(fid);
end
function restore(oldpath,working)
path(oldpath);rmdir(working,'s');
end

