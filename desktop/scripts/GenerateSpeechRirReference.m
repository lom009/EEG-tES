function GenerateSpeechRirReference(researchRoot, outputPath, oracleDirectory)
% Offline numerical oracle only. Calls the existing research MEX without its GUI.
% Generated data, not GPL source, is used by runtime-independent tests.
oldpath = path; cleanup = onCleanup(@() path(oldpath));
addpath(fullfile(researchRoot,'SpeechIntelligibility','RIR-Generator-master'));
% The bundled 2019 MEX may not load on newer MATLAB. Compile the unchanged
% research source into a disposable oracle directory; never ship this binary.
if nargin < 3
    working = tempname; mkdir(working);
    cleanupWorking = onCleanup(@() cleanupOracle(working));
    mex('-outdir',working,fullfile(researchRoot,'SpeechIntelligibility','RIR-Generator-master','rir_generator.cpp'));
    addpath(working,'-begin');
else
    addpath(oracleDirectory,'-begin');
end
cases = {};
specs = { [2 3.5 2], .6, .6; [2.13 3.21 1.84], .3, .35; [1.41 2.37 1.79], .15, .2; [2 3.5 2], 0, .1 };
for i=1:size(specs,1)
    room = [5 4 6]; listener = [2 1.5 2]; source = specs{i,1};
    t60 = specs{i,2}; len = specs{i,3};
    h = rir_generator(340,16000,listener,source,room,t60,round(max(t60,len)*16000));
    h = h/max(h); [~,direct] = max(h);
    x = .12*sin((0:64)*2*pi*431/16000);
    convolution = conv(x,h);
    cases{end+1} = struct('source',source,'t60',t60,'length',len,'directFrame',direct-1,'rir',h,'convolution',convolution);
end
f = fopen(outputPath,'w'); cleanupFile = onCleanup(@() fclose(f));
fwrite(f,jsonencode(struct('generator','Research rir_generator 2.1.20141124; 16 kHz, 340 m/s, default omnidirectional/high-pass','cases',{cases})),'char');
end
function cleanupOracle(working)
clear rir_generator;
rmdir(working,'s');
end
