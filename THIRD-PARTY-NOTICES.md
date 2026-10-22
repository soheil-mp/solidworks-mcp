# Third-party notices

solidworks-mcp is MIT-licensed (see LICENSE). Binary builds include the following components:

| Component | License | Source |
|---|---|---|
| ModelContextProtocol, ModelContextProtocol.Core (2.3.0) | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.Hosting, .Logging, .DependencyInjection, .AI.Abstractions and their dependencies (10.x) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/extensions |
| .NET runtime (self-contained builds only) | MIT | https://github.com/dotnet/runtime |

The Apache License 2.0 text is available at https://www.apache.org/licenses/LICENSE-2.0.

**SOLIDWORKS interop types.** At build time the compiler embeds the COM type information the server uses from the
SOLIDWORKS interop assemblies of the *builder's own* SOLIDWORKS installation (`api\redist`). No Dassault Systèmes
binaries, type libraries or documentation are included in this repository. SOLIDWORKS is a trademark of Dassault
Systèmes SolidWorks Corporation; this project is not affiliated with Dassault Systèmes.
