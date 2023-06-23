﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NumSharp;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.DiffSinger {
    public class DiffSingerRenderer : IRenderer {
        const float headMs = DiffSingerUtils.headMs;
        const float tailMs = DiffSingerUtils.tailMs;
        const string VELC = DiffSingerUtils.VELC;
        const string VoiceColorHeader = DiffSingerUtils.VoiceColorHeader;

        static readonly HashSet<string> supportedExp = new HashSet<string>(){
            Format.Ustx.DYN,
            Format.Ustx.PITD,
            Format.Ustx.GENC,
            Format.Ustx.CLR,
            VELC,
        };

        static readonly object lockObj = new object();

        public USingerType SingerType => USingerType.DiffSinger;

        public bool SupportsRenderPitch => false;

        public bool IsVoiceColorCurve(string abbr, out int subBankId) {
            subBankId = 0;
            if (abbr.StartsWith(VoiceColorHeader) && int.TryParse(abbr.Substring(2), out subBankId)) {;
                subBankId -= 1;
                return true;
            } else {
                return false;
            }
        }

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return supportedExp.Contains(descriptor.abbr) || 
                (descriptor.abbr.StartsWith(VoiceColorHeader) && int.TryParse(descriptor.abbr.Substring(2), out int _));
        }

        //Calculate the Timing layout of the RenderPhrase, 
        //including the position of the phrase, 
        //the length of the head consonant, and the estimated total length
        public RenderResult Layout(RenderPhrase phrase) {
            return new RenderResult() {
                leadingMs = headMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = headMs + phrase.durationMs + tailMs,
            };
        }

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, CancellationTokenSource cancellation, bool isPreRender) {
            var task = Task.Run(() => {
                lock (lockObj) {
                    if (cancellation.IsCancellationRequested) {
                        return new RenderResult();
                    }
                    var result = Layout(phrase);
                    int speedup = Core.Util.Preferences.Default.DiffsingerSpeedup;
                    var wavPath = Path.Join(PathManager.Inst.CachePath, $"ds-{phrase.hash:x16}-{speedup}x.wav");
                    string progressInfo = $"{this}{speedup}x \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
                    if (File.Exists(wavPath)) {
                        try {
                            using (var waveStream = Wave.OpenFile(wavPath)) {
                                result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                            }
                        } catch (Exception e) {
                            Log.Error(e, "Failed to render.");
                        }
                    }
                    if (result.samples == null) {
                        result.samples = InvokeDiffsinger(phrase, speedup);
                        var source = new WaveSource(0, 0, 0, 1);
                        source.SetSamples(result.samples);
                        WaveFileWriter.CreateWaveFile16(wavPath, new ExportAdapter(source).ToMono(1, 0));
                    }
                    if (result.samples != null) {
                        Renderers.ApplyDynamics(phrase, result);
                    }
                    progress.Complete(phrase.phones.Length, progressInfo);
                    return result;
                }
            });
            return task;
        }
        /*result format: 
        result.samples: Rendered audio, float[]
        leadingMs、positionMs、estimatedLengthMs: timeaxis layout in Ms, double
         */

        float[] InvokeDiffsinger(RenderPhrase phrase,int speedup) {
            var singer = phrase.singer as DiffSingerSinger;
            //Check if dsconfig.yaml is correct
            if(String.IsNullOrEmpty(singer.dsConfig.vocoder) ||
                String.IsNullOrEmpty(singer.dsConfig.acoustic) ||
                String.IsNullOrEmpty(singer.dsConfig.phonemes)){
                throw new Exception("Invalid dsconfig.yaml. Please ensure that dsconfig.yaml contains keys \"vocoder\", \"acoustic\" and \"phonemes\".");
            }

            var vocoder = singer.getVocoder();
            var frameMs = vocoder.frameMs();
            var frameSec = frameMs / 1000;
            int headFrames = (int)(headMs / frameMs);
            int tailFrames = (int)(tailMs / frameMs);
            var result = Layout(phrase);
            //acoustic
            //mel = session.run(['mel'], {'tokens': tokens, 'durations': durations, 'f0': f0, 'speedup': speedup})[0]
            //tokens: phoneme index in the phoneme set
            //durations: phoneme duration in frames
            //f0: pitch curve in Hz by frame
            //speedup: Diffusion render speedup, int
            var tokens = phrase.phones
                .Select(p => p.phoneme)
                .Prepend("SP")
                .Append("SP")
                .Select(x => (long)(singer.phonemes.IndexOf(x)))
                .ToList();
            var durations = phrase.phones
                .Select(p => (int)(p.endMs / frameMs) - (int)(p.positionMs / frameMs))//prevent cumulative error
                .Prepend(headFrames)
                .Append(tailFrames)
                .ToList();
            var totalFrames = (int)(durations.Sum());
            float[] f0 = DiffSingerUtils.SampleCurve(phrase, phrase.pitches, 0, frameMs, totalFrames, headFrames, tailFrames, 
                x => MusicMath.ToneToFreq(x * 0.01))
                .Select(f => (float)f).ToArray();
            //toneShift isn't supported

            var acousticInputs = new List<NamedOnnxValue>();
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("tokens",
                new DenseTensor<long>(tokens.ToArray(), new int[] { tokens.Count },false)
                .Reshape(new int[] { 1, tokens.Count })));
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("durations",
                new DenseTensor<long>(durations.Select(x=>(long)x).ToArray(), new int[] { durations.Count }, false)
                .Reshape(new int[] { 1, durations.Count })));
            var f0tensor = new DenseTensor<float>(f0, new int[] { f0.Length })
                .Reshape(new int[] { 1, f0.Length });
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("f0",f0tensor));
            acousticInputs.Add(NamedOnnxValue.CreateFromTensor("speedup",
                new DenseTensor<long>(new long[] { speedup }, new int[] { 1 },false)));

            //speaker
            if(singer.dsConfig.speakers != null) {
                var speakers = singer.dsConfig.speakers;
                var hiddenSize = singer.dsConfig.hiddenSize;
                var speakerEmbeds = singer.getSpeakerEmbeds();
                //get default speaker
                var headDefaultSpk = speakers.IndexOf(phrase.phones[0].suffix);
                var tailDefaultSpk = speakers.IndexOf(phrase.phones[^1].suffix);
                var defaultSpkByFrame = Enumerable.Repeat(headDefaultSpk, headFrames).ToList(); 
                defaultSpkByFrame.AddRange(Enumerable.Range(0, phrase.phones.Length)
                    .SelectMany(phIndex => Enumerable.Repeat(speakers.IndexOf(phrase.phones[phIndex].suffix), durations[phIndex+1])));
                defaultSpkByFrame.AddRange(Enumerable.Repeat(tailDefaultSpk, tailFrames));
                //get speaker curves
                NDArray spkCurves = np.zeros<float>(totalFrames, speakers.Count);
                foreach(var curve in phrase.curves) {
                    if(IsVoiceColorCurve(curve.Item1,out int subBankId) && subBankId < singer.Subbanks.Count) {
                        var spkId = speakers.IndexOf(singer.Subbanks[subBankId].Suffix);
                        spkCurves[":", spkId] = DiffSingerUtils.SampleCurve(phrase, curve.Item2, 0, 
                            frameMs, totalFrames, headFrames, tailFrames, x => x * 0.01f)
                            .Select(f => (float)f).ToArray();
                    }
                }
                foreach(int frameId in Enumerable.Range(0,totalFrames)) {
                    //standarization
                    var spkSum = spkCurves[frameId, ":"].ToArray<float>().Sum();
                    if (spkSum > 1) {
                        spkCurves[frameId, ":"] /= spkSum;
                    } else {
                        spkCurves[frameId,defaultSpkByFrame[frameId]] += 1 - spkSum;
                    }
                }
                var spkEmbedResult = np.dot(spkCurves, speakerEmbeds.T);
                var spkEmbedTensor = new DenseTensor<float>(spkEmbedResult.ToArray<float>(), 
                    new int[] { totalFrames, hiddenSize })
                    .Reshape(new int[] { 1, totalFrames, hiddenSize });
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("spk_embed", spkEmbedTensor));
            }
            //gender
            //Definition of GENC: 100 = 12 semitones of formant shift, positive GENC means shift down
            if (singer.dsConfig.useKeyShiftEmbed) {
                var range = singer.dsConfig.augmentationArgs.randomPitchShifting.range;
                var positiveScale = (range[1]==0) ? 0 : (12/range[1]/100);
                var negativeScale = (range[0]==0) ? 0 : (-12/range[0]/100);
                float[] gender = DiffSingerUtils.SampleCurve(phrase, phrase.gender, 
                    0, frameMs, totalFrames, headFrames, tailFrames,
                    x=> (x<0)?(-x * positiveScale):(-x * negativeScale))
                    .Select(f => (float)f).ToArray();
                var genderTensor = new DenseTensor<float>(gender, new int[] { gender.Length })
                    .Reshape(new int[] { 1, gender.Length });
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("gender", genderTensor));
            }

            //velocity
            //Definition of VELC: logarithmic scale, Default value 100 = original speed, 
            //each 100 increase means speed x2
            if (singer.dsConfig.useSpeedEmbed) {
                var velocityCurve = phrase.curves.FirstOrDefault(curve => curve.Item1 == VELC);
                float[] velocity;
                if (velocityCurve != null) {
                    velocity = DiffSingerUtils.SampleCurve(phrase, velocityCurve.Item2,
                        1, frameMs, totalFrames, headFrames, tailFrames,
                        x => Math.Pow(2, (x - 100) / 100))
                        .Select(f => (float)f).ToArray();
                } else {
                    velocity = Enumerable.Repeat(1f, totalFrames).ToArray();
                }
                var velocityTensor = new DenseTensor<float>(velocity, new int[] { velocity.Length })
                    .Reshape(new int[] { 1, velocity.Length });
                acousticInputs.Add(NamedOnnxValue.CreateFromTensor("velocity", velocityTensor));
            }

            Tensor<float> mel;
            var acousticOutputs = singer.getAcousticSession().Run(acousticInputs);
            mel = acousticOutputs.Clone();
            
            //vocoder
            //waveform = session.run(['waveform'], {'mel': mel, 'f0': f0})[0]
            var vocoderInputs = new List<NamedOnnxValue>();
            vocoderInputs.Add(NamedOnnxValue.CreateFromTensor("mel", mel));
            vocoderInputs.Add(NamedOnnxValue.CreateFromTensor("f0",f0tensor));
            float[] samples;
            var vocoderOutputs = vocoder.session.Run(vocoderInputs).ToArray();

            return vocoderOutputs;
        }


        //Loading rendered pitch isn't currently supported
        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            return null;
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) {
            var result = new List<UExpressionDescriptor> {
                new UExpressionDescriptor{
                    name="velocity (curve)",
                    abbr=VELC,
                    type=UExpressionType.Curve,
                    min=0,
                    max=200,
                    defaultValue=100,
                    isFlag=false,
                }
            };
            var dsSinger = singer as DiffSingerSinger;
            if(dsSinger!=null && dsSinger.dsConfig.speakers != null) {
                result.AddRange(Enumerable.Zip(
                    dsSinger.Subbanks,
                    Enumerable.Range(1, dsSinger.Subbanks.Count),
                    (subbank,index)=>new UExpressionDescriptor {
                        name=$"voice color {subbank.Color}",
                        abbr=VoiceColorHeader+index.ToString("D2"),
                        type=UExpressionType.Curve,
                        min=0,
                        max=100,
                        defaultValue=0,
                        isFlag=false,
                    }));
            }
            return result.ToArray();
        }

        public override string ToString() => Renderers.DIFFSINGER;
    }
}
