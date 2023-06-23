using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json;
using OpenUtau.Core.Util;
using Vortice.DXGI;

namespace OpenUtau.Core {
    public class GpuInfo {
        public int deviceId;
        public string description = "";

        override public string ToString() {
            return $"[{deviceId}] {description}";
        }
    }

    public abstract class IOnnxInferenceSession {
        public abstract Tensor<float> Run(List<NamedOnnxValue> inputs);
    }

    class LocalInferenceSession : IOnnxInferenceSession {
        InferenceSession session;
        public LocalInferenceSession(InferenceSession session) {
            this.session = session;
        }

        public override Tensor<float> Run(List<NamedOnnxValue> inputs) {
            return session.Run(inputs).First().AsTensor<float>();
        }
    }

    class RemoteInferenceSession : IOnnxInferenceSession {
        class RemoteInput {
            public string type; // Not used for now
            public int[] shape;
            public float[] data;
        }

        string url;
        public RemoteInferenceSession(string url) {
            this.url = url;
        }

        public override Tensor<float> Run(List<NamedOnnxValue> inputs) {
            // Convert inputs to RemoteInput
            Dictionary<string, RemoteInput> remoteInputs = new Dictionary<string, RemoteInput>();

            foreach (NamedOnnxValue input in inputs) {
                try {
                    remoteInputs.Add(input.Name, new RemoteInput {
                        type = "float",
                        shape = input.AsTensor<float>().Dimensions.ToArray(),
                        data = input.AsTensor<float>().ToArray()
                    });
                } catch (Exception e) {
                    // Try long
                    remoteInputs.Add(input.Name, new RemoteInput {
                        type = "long",
                        shape = input.AsTensor<long>().Dimensions.ToArray(),
                        data = input.AsTensor<long>().Select(x => (float)x).ToArray()
                    });
                }
            }

            // Send request
            string reqStr = JsonConvert.SerializeObject(remoteInputs);
            
            using (var client = new System.Net.WebClient()) {
                client.Headers.Add("Content-Type", "application/json");
                client.Encoding = System.Text.Encoding.UTF8;
                string resStr = client.UploadString(url, reqStr);
                var resData = JsonConvert.DeserializeObject<RemoteInput>(resStr);
                
                // Only float32 is supported for now
                return new DenseTensor<float>(resData.data, resData.shape);
            }
        }
    }

    public class Onnx {
        public static List<string> getRunnerOptions() {
            if (OS.IsWindows()) {
                return new List<string> {
                "cpu",
                "directml"
                };
            } else if (OS.IsMacOS()) {
                return new List<string> {
                "cpu",
                "coreml"
                };
            }
            return new List<string> {
                "cpu"
            };
        }

        public static List<GpuInfo> getGpuInfo() {
            List<GpuInfo> gpuList = new List<GpuInfo>();
            if (OS.IsWindows()) {
                DXGI.CreateDXGIFactory1(out IDXGIFactory1 factory);
                for(int deviceId = 0; deviceId < 32; deviceId++) {
                    factory.EnumAdapters1(deviceId, out IDXGIAdapter1 adapterOut);
                    if(adapterOut is null) {
                        break;
                    }
                    gpuList.Add(new GpuInfo {
                        deviceId = deviceId,
                        description = adapterOut.Description.Description
                    }) ;
                }
            }
            if (gpuList.Count == 0) {
                gpuList.Add(new GpuInfo {
                    deviceId = 0,
                });
            }
            return gpuList;
        }

        public static IOnnxInferenceSession getLocalInferenceSession(byte[] model) {
            SessionOptions options = new SessionOptions();
            List<string> runnerOptions = getRunnerOptions();
            string runner = Preferences.Default.OnnxRunner;
            if (String.IsNullOrEmpty(runner)) {
                runner = runnerOptions[0];
            }
            if (!(runnerOptions.Contains(runner))) {
                runner = "cpu";
            }
            switch(runner){
                case "directml":
                    options.AppendExecutionProvider_DML(Preferences.Default.OnnxGpu);
                    break;
                case "coreml":
                    options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);
                    break;
            }

            return new LocalInferenceSession(new InferenceSession(model, options));
        }
    
        public static IOnnxInferenceSession getRemoteInferenceSession(string url) {
            return new RemoteInferenceSession(url);
        }
    }
}
