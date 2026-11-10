# WebGPU memory comparison

Diagnostic builds for the serialized `2+2` and `3+3` comparison requested in
[ONNX Runtime issue 33803](https://github.com/microsoft/onnxruntime/issues/33803#issuecomment-6763107027).

Based on SmartStack `main` commit `e4ba0b4fc890937959e7c379b847da719bf4eb39`.

## Create a separate branch

Run this in your existing SmartStack checkout, then copy the archive's
`ONNX_Runner` directory into the repository root, merging the directories:

```powershell
git switch -c diagnostics/webgpu-memory-comparison
```

## Build both Linux variants

Run these commands from `ONNX_Runner`, using .NET 10. They work in Windows
PowerShell and on Linux. Build the profiles one after the other.

```powershell
dotnet publish ONNX_Runner.csproj -c Release -r linux-x64 -p:UseWebGpu=true -p:WebGpuMemoryComparison=Legacy --self-contained true -p:PublishSingleFile=true -o ./bin/publish/webgpu-memory-legacy
```

```powershell
dotnet publish ONNX_Runner.csproj -c Release -r linux-x64 -p:UseWebGpu=true -p:WebGpuMemoryComparison=Current --self-contained true -p:PublishSingleFile=true -o ./bin/publish/webgpu-memory-current
```

| Profile | ORT | WebGPU EP | Inference |
| --- | --- | --- | --- |
| `Legacy` | 1.29.0 | 0.3.0 | Process-wide serialization |
| `Current` | 1.31.1 | 0.5.0 | Process-wide serialization |

Both profiles use `maxNumPendingDispatches=1` and `storageBufferCacheMode=bucket`.
The same shared gate covers Piper, tone extraction, tone conversion, output
copying, and native output disposal. Model sessions stay loaded; pool sizes are
unchanged. The single extractor remains loaded during baseline generation and
is unloaded at the usual startup stage.

The comparison rejects CPU routing for Piper, disabled cloning, mismatched GPU
indexes, and GPU initialization errors. GPU initialization errors stop the
process instead of trying another adapter or falling back to CPU.

A build without `WebGpuMemoryComparison` keeps the normal production behavior
and the current package versions.

## Prepare the Linux test

Place the two published directories side by side on the Lubuntu machine, named
`webgpu-memory-legacy` and `webgpu-memory-current`. Use identical `Model`,
`Cloner`, `Voices`, `appsettings.json`, and text-rule files in both directories.
Copy model and voice files from the working installation if they are missing
from the published folders.

Open Bash in the parent directory of the two builds:

```bash
chmod +x ./webgpu-memory-legacy/TsubakiTTS ./webgpu-memory-current/TsubakiTTS
unset ORT_WEBGPU_EP_FORCE_LEGACY
set -o pipefail
```

The commands below select the NVIDIA Vulkan ICD previously confirmed on this
machine. WebGPU 0.3.0 has no `adapterIndex` provider option, so its comparison
profile accepts index 0 and relies on this operating-system selection. The
same NVIDIA-only selection is applied to both profiles.

## Record GPU memory

Before each server launch, run this in a second terminal. Change the output name
to match the case: `legacy-2+2`, `legacy-3+3`, `current-2+2`, or `current-3+3`.
Stop the sampler with Ctrl+C after that case finishes.

```bash
nvidia-smi --query-gpu=timestamp,name,pci.bus_id,memory.used,memory.total --format=csv -lms 100 > legacy-2+2-memory.csv
```

The CSV contains sampled device memory usage, including other GPU users.
Keep other GPU workloads consistent between cases.

## Run four fresh processes

`2+2` means two Piper sessions and two OpenVoice color-converter sessions;
`3+3` means three of each. Stop a successfully started server with Ctrl+C
before launching the next case. Each launch repeats the existing baseline
generation and both HTTP warm-ups with the same application code.

### Legacy, 2+2

```bash
VK_LOADER_DRIVERS_SELECT=nvidia_icd.json ./webgpu-memory-legacy/TsubakiTTS --HardwareSettings:ForcePiperToCpu false --HardwareSettings:PiperGpuDeviceId 0 --HardwareSettings:OpenVoiceGpuDeviceId 0 --HardwareSettings:MaxConcurrentGpuRequests 2 --ClonerSettings:EnableCloning true 2>&1 | tee legacy-2+2.log
```

### Legacy, 3+3

```bash
VK_LOADER_DRIVERS_SELECT=nvidia_icd.json ./webgpu-memory-legacy/TsubakiTTS --HardwareSettings:ForcePiperToCpu false --HardwareSettings:PiperGpuDeviceId 0 --HardwareSettings:OpenVoiceGpuDeviceId 0 --HardwareSettings:MaxConcurrentGpuRequests 3 --ClonerSettings:EnableCloning true 2>&1 | tee legacy-3+3.log
```

### Current, 2+2

```bash
VK_LOADER_DRIVERS_SELECT=nvidia_icd.json ./webgpu-memory-current/TsubakiTTS --HardwareSettings:ForcePiperToCpu false --HardwareSettings:PiperGpuDeviceId 0 --HardwareSettings:OpenVoiceGpuDeviceId 0 --HardwareSettings:MaxConcurrentGpuRequests 2 --ClonerSettings:EnableCloning true 2>&1 | tee current-2+2.log
```

### Current, 3+3

```bash
VK_LOADER_DRIVERS_SELECT=nvidia_icd.json ./webgpu-memory-current/TsubakiTTS --HardwareSettings:ForcePiperToCpu false --HardwareSettings:PiperGpuDeviceId 0 --HardwareSettings:OpenVoiceGpuDeviceId 0 --HardwareSettings:MaxConcurrentGpuRequests 3 --ClonerSettings:EnableCloning true 2>&1 | tee current-3+3.log
```

Look for the `[DIAGNOSTIC] WebGPU memory comparison` line with the expected
runtime, EP version, and `GPU Run calls=serialized`. Both hardware lines must
report GPU execution and the requested session count.

Record whether baseline generation completed, the number of successful
warm-ups, the failure location if any, and the largest sampled GPU-memory
value. A warm-up result below `2/2` is a failed warm-up case, even if the server
continues listening. Preserve the complete logs and matching memory CSVs.

## Automated checks without a GPU

These checks use tiny native CPU ONNX fixtures through the production inference
methods. They verify that all three paths honor the same gate, native errors
release it, and pools retain two or three resident sessions. They also check
runtime validation and rejection of incompatible startup settings.

```powershell
dotnet run --project Tests/WebGpuMemoryComparisonRegression/WebGpuMemoryComparisonRegression.csproj -c Release -p:UseWebGpu=true -p:WebGpuMemoryComparison=Legacy
```

```powershell
dotnet run --project Tests/WebGpuMemoryComparisonRegression/WebGpuMemoryComparisonRegression.csproj -c Release -p:UseWebGpu=true -p:WebGpuMemoryComparison=Current
```

The real Vulkan memory comparison is performed by the four Linux launches above.
