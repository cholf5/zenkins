# Third-party notices

Zenkins is distributed with the third-party components listed here. Each section names the component,
its license, and, where the component's own distribution omits it, the full license text.

## MoonSharp (`MoonSharp.Interpreter`)

- Package: `MoonSharp` 2.0.0 (`lib/netstandard1.6/MoonSharp.Interpreter.dll`), pinned in
  `Directory.Packages.props` and referenced by `Zenkins.Worker` only.
- Upstream: <https://github.com/moonsharp-devs/moonsharp>
- License: BSD-3-Clause, plus the MIT-licensed parts and icon attributions reproduced below.

The 2.0.0 package ships no `LICENSE` file at all (its nuspec carries only a `licenseUrl`), so the
text below is copied verbatim from the upstream `LICENSE` file as shipped in the MoonSharp
`3.0.0-beta.1` package, which is the same license. It is reproduced in full, including the
`{organization}` placeholder: substituting our own name would misquote the license.

```text
Copyright (c) 2014-2016, Marco Mastropaolo
All rights reserved.

Parts of the string library are based on the KopiLua project (https://github.com/NLua/KopiLua)
Copyright (c) 2012 LoDC

Visual Studio Code debugger code is based on code from Microsoft vscode-mono-debug project (https://github.com/Microsoft/vscode-mono-debug).
Copyright (c) Microsoft Corporation - released under MIT license.

Remote Debugger icons are from the Eclipse project (https://www.eclipse.org/).
Copyright of The Eclipse Foundation

The MoonSharp icon is (c) Isaac, 2014-2015

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the {organization} nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Copyright

Copyright (c) 2014-2016, Marco Mastropaolo
All rights reserved.

### KopiLua string library (MIT)

Parts of the MoonSharp string library are based on the KopiLua project
(<https://github.com/NLua/KopiLua>), Copyright (c) 2012 LoDC. The upstream `LICENSE` file names
KopiLua but does not reproduce the MIT text it is licensed under; the full text follows.

MIT License

Copyright (c) 2012 LoDC

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

### Microsoft `vscode-mono-debug` (MIT)

The Visual Studio Code debugger code is based on code from the Microsoft `vscode-mono-debug` project
(<https://github.com/Microsoft/vscode-mono-debug>), Copyright (c) Microsoft Corporation - released
under MIT license. The upstream `LICENSE` file names the project but does not reproduce the MIT
text; the full text follows.

MIT License

Copyright (c) Microsoft Corporation

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

### Eclipse icons

Remote Debugger icons are from the Eclipse project (<https://www.eclipse.org/>).
Copyright of The Eclipse Foundation.

Upstream states no license for these icons, and Zenkins does not redistribute them: the debugger
console, the REPL and the VS Code extension that carry them are outside the package scope below.

### MoonSharp icon

The MoonSharp icon is (c) Isaac, 2014-2015.

## Scope of redistribution

Zenkins redistributes exactly one assembly from the MoonSharp package: `MoonSharp.Interpreter`. No
`MoonSharp.Debugger`/`MoonSharp.REPL` console, no editor/VS Code extension and no other file from
the package is redistributed, so the Eclipse icons above are referenced for completeness only.
