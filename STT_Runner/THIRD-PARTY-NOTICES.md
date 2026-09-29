# Third-party licenses and notices

This file covers third-party components used by Mwandishi STT. Package versions
match the project file and the current NuGet dependency graph. The project does
not claim ownership of these components. Model files are downloaded on first
launch and are not included in the release archives.

## NuGet dependencies

| Package | Version | License | Upstream |
| --- | --- | --- | --- |
| Microsoft.AspNetCore.OpenApi | 10.0.7 | MIT | https://github.com/dotnet/aspnetcore |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.ApiDescription.Server | 10.0.0 | MIT | https://github.com/dotnet/aspnetcore |
| Microsoft.ML.OnnxRuntime | 1.25.1 | MIT | https://github.com/microsoft/onnxruntime |
| Microsoft.ML.OnnxRuntime.Managed | 1.25.1 | MIT | https://github.com/microsoft/onnxruntime |
| Microsoft.OpenApi | 2.7.5 | MIT | https://github.com/microsoft/OpenAPI.NET |
| Swashbuckle.AspNetCore | 10.1.7 | MIT | https://github.com/domaindrivendev/Swashbuckle.AspNetCore |
| Swashbuckle.AspNetCore.Swagger | 10.1.7 | MIT | https://github.com/domaindrivendev/Swashbuckle.AspNetCore |
| Swashbuckle.AspNetCore.SwaggerGen | 10.1.7 | MIT | https://github.com/domaindrivendev/Swashbuckle.AspNetCore |
| Swashbuckle.AspNetCore.SwaggerUI | 10.1.7 | MIT | https://github.com/domaindrivendev/Swashbuckle.AspNetCore |
| System.Numerics.Tensors | 9.0.0 | MIT | https://github.com/dotnet/runtime |
| Whisper.net | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime.Metal | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |

The Metal package is a transitive dependency in the restored graph; the
Windows and Linux archives do not use its native macOS assets. The relevant
notices include Copyright (c) Microsoft Corporation, Copyright (c) 2016
Richard Morris (Swashbuckle), and Copyright (c) 2024 sandrohanea
(Whisper.net). The MIT terms below apply to the listed packages; individual
package notices in their source distributions remain applicable.

## Native Whisper runtime and models

Whisper.net's native runtimes include whisper.cpp and ggml, both distributed
under MIT. See https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE and
https://github.com/ggml-org/ggml/blob/master/LICENSE. Copyright belongs to the
respective contributors, including Copyright (c) 2023-2026 The ggml authors.

The default `ggml-base.bin` is a converted Whisper model. OpenAI publishes
Whisper code and model weights under MIT:
https://github.com/openai/whisper/blob/main/LICENSE. The default
`silero_vad.onnx` is based on Silero VAD, also MIT:
https://github.com/snakers4/silero-vad/blob/master/LICENSE. Both defaults are
served by https://huggingface.co/Hinotsuba/silero_vad_ggml-base. If you change
the model source, check that model's own license before distributing it.
The upstream notices include Copyright (c) 2022 OpenAI and Copyright (c)
2020-present Silero Team.

## FFmpeg in packaged releases

`Build-Releases.ps1` packages only the `ffmpeg` executable from pinned BtbN
Windows and Linux x64 LGPL builds. FFmpeg's base license is LGPL-2.1-or-later;
the license of an actual build depends on its configuration and optional
libraries. These release archives use the upstream `lgpl` variant, not its
separate `gpl` variant. The full LGPL 2.1 text is in
`Licenses/FFmpeg-LGPL-2.1.txt`. The script also copies the provider's available
license files and build metadata to `Licenses/FFmpeg-build/` and writes the
exact binary source URL and SHA-256 to `Licenses/FFmpeg-build/PROVENANCE.txt`.
Source code and build recipes must remain available for the exact FFmpeg
binary distributed; see https://ffmpeg.org/legal.html and
https://github.com/BtbN/FFmpeg-Builds. Check the generated provenance and the
build's configuration before public distribution.

FFmpeg is not a NuGet dependency. The program runs the bundled executable as
a separate process. Direct development builds may instead use FFmpeg on PATH.

## Warm-up recording

`Assets/warmup.wav` is a 16 kHz mono conversion of "French Canadian Woman Giving
Instructions 04.wav" by vero.marengere, from
https://freesound.org/people/vero.marengere/sounds/514877/. The source sound is
offered under Creative Commons Zero (CC0 1.0):
https://creativecommons.org/publicdomain/zero/1.0/.

## MIT license text

The following terms are provided for the MIT components listed above, with
their respective copyright notices and authors identified in their upstream
repositories and package metadata.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
