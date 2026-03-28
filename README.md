# simple-vc-lib
**simple-vc-lib** is a multi-language library that provides an agnostic wrapper around common version control systems for game development tools. It lets your tools create, edit, and delete files without needing to know or care which version control system the user has in place.

```javascript
import { prepareToWrite, finishedWrite, deleteFile } from './simpleVcLib.js';

// Before writing - checks out the file if needed, removes read-only flag
const prep = prepareToWrite('/path/to/dialogue.json');
if (!prep.success) {
    console.error(prep.message); // e.g. "File is locked by another user"
    process.exit(1);
}

// ... write your file however you like ...

// After writing - adds to VC if it's a new file
finishedWrite('/path/to/dialogue.json');

// Deleting - marks for deletion in VC if tracked
deleteFile('/path/to/old-dialogue.json');
```

```csharp
using SimpleVCLib;

// Before writing
var prep = VCLib.PrepareToWrite("/path/to/dialogue.json");
if (!prep.Success) {
    Console.WriteLine(prep.Message); // e.g. "File is locked by another user"
    return;
}

// ... write your file however you like ...

// After writing
VCLib.FinishedWrite("/path/to/dialogue.json");

// Deleting
VCLib.DeleteFile("/path/to/old-dialogue.json");
```

### Contents
* [What it does](#what-it-does)
* [Source Code](#source-code)
* [Releases](#releases)
* [Supported Version Control Systems](#supported-version-control-systems)
* [Usage](#usage)
    * [Overview](#overview)
    * [Operations](#operations)
    * [Return Values](#return-values)
    * [VC Detection](#vc-detection)
    * [Overriding VC Detection](#overriding-vc-detection)
    * [Javascript](#javascript)
    * [C#](#c)
* [Contributors](#contributors)
* [License](#license)

## What it does
Game development tools often create, modify, or delete content files — dialogue JSON, audio files, configuration — while the project is under version control. If a file is checked in to Perforce or Plastic SCM it will typically be read-only until checked out, and new files need to be explicitly added.

**simple-vc-lib** handles all of that for you behind a single consistent API, so you can write one piece of code that works regardless of whether the user is on Git, Perforce, Plastic SCM, SVN, or no version control at all.

## Source Code
The source can be found on [Github](https://github.com/wildwinter/simple-vc-lib), and is available under the MIT license.

## Releases
Releases are available in the releases area on [Github](https://github.com/wildwinter/simple-vc-lib/releases):
* **Javascript** — an ESM module (`simpleVcLib.js`) and a CommonJS module (`simpleVcLib.cjs`) for use in Node.js tools.
* **C#** — a .NET 8 DLL (`SimpleVCLib.dll`) for use in any C# project.

Both are cross-platform (macOS Arm64 and Windows x64).

## Supported Version Control Systems
| System | CLI used | Detection |
|---|---|---|
| **Git** | `git` | `.git` folder/file walking up from the file path |
| **Perforce (Helix Core)** | `p4` | `p4 info` command succeeds with a configured workspace |
| **Plastic SCM / Unity Version Control** | `cm` | `.plastic` folder walking up from the file path |
| **SVN (Subversion)** | `svn` | `.svn` folder walking up from the file path |
| **Filesystem (no VC)** | — | Fallback when nothing else is detected |

The library calls the relevant CLI under the hood. The appropriate CLI tool must be installed and on the system PATH.

## Usage

### Overview
* Call **`prepareToWrite`** before you write to a file. If the file doesn't yet exist, this is a no-op. If it exists and is read-only (e.g. checked in to Perforce), it will be checked out / unlocked. If it can't be made writable, you get a failure result.
* Write the file using whatever mechanism you prefer.
* Call **`finishedWrite`** after writing. If the file is new and not yet tracked by VC, it will be added. For already-tracked files this is a no-op.
* Call **`deleteFile`** or **`deleteFolder`** to remove files. Tracked files will be marked for deletion in the VC system; untracked files are just deleted from disk.

You don't need to tell the library which VC system is in use — it detects this automatically. See [VC Detection](#vc-detection) below.

### Operations

#### `prepareToWrite(filePath)`
Prepares a file path for writing.
- If the file does not yet exist: succeeds immediately (ready to create).
- If the file exists and is writable: succeeds immediately.
- If the file exists and is read-only: checks it out (Perforce, Plastic SCM, SVN) or removes the read-only attribute (Git, filesystem).

On failure, the `status` field indicates the reason: `locked` means the file is exclusively held by another user; `outOfDate` means the local copy is behind the depot and needs syncing first.

#### `finishedWrite(filePath)`
Notifies the library that a file has been written.
- If the file is newly created (not yet in VC): adds it (`git add`, `p4 add`, `cm add`, `svn add`).
- If the file was already tracked: no-op.

#### `deleteFile(filePath)`
Deletes a file, scheduling it for deletion in VC if it is tracked.

#### `deleteFolder(folderPath)`
Deletes a folder and all its contents. Tracked files are scheduled for VC deletion; untracked files are deleted from disk.

### Return Values
All four operations return a result object with three fields:

| Field | Type | Description |
|---|---|---|
| `success` | `bool` | `true` if the operation succeeded |
| `status` | string/enum | `ok`, `locked`, `outOfDate`, or `error` |
| `message` | string | Human-readable detail, especially on failure |

`locked` and `outOfDate` are only produced by `prepareToWrite`, for VC systems that support exclusive locking or require syncing before editing.

### VC Detection
The library detects the active VC system automatically, in this order:

1. **`SIMPLE_VC` environment variable** — set this to `git`, `perforce`, `plastic`, `svn`, or `filesystem` to skip auto-detection entirely.
2. **`.vcconfig` file** — a JSON file placed anywhere in the directory tree above the file being operated on:
    ```json
    { "system": "perforce" }
    ```
3. **Marker directories** — the library walks up from the file's directory looking for `.git`, `.plastic`, or `.svn`.
4. **Perforce** — runs `p4 info` to check whether a Perforce workspace is configured.
5. **Filesystem fallback** — if nothing is detected, the library operates on plain files with no VC interaction. Read-only files are still handled by removing the read-only attribute.

### Overriding VC Detection
If auto-detection is unreliable in your environment, you can also force a specific provider in code:

**Javascript:**
```javascript
import { setProvider, clearProvider, GitProvider } from './simpleVcLib.js';

setProvider(new GitProvider()); // Force Git for all operations
// ...
clearProvider();                // Restore auto-detection
```

**C#:**
```csharp
using SimpleVCLib;

VCLib.SetProvider(new GitProvider()); // Force Git for all operations
// ...
VCLib.ClearProvider();                // Restore auto-detection
```

Available provider classes: `GitProvider`, `PerforceProvider`, `PlasticProvider`, `SvnProvider`, `FilesystemProvider`.

### Javascript
Add `simpleVcLib.js` (ESM) or `simpleVcLib.cjs` (CommonJS) to your project.

```javascript
// ESM
import { prepareToWrite, finishedWrite, deleteFile, deleteFolder } from './simpleVcLib.js';

const prep = prepareToWrite('/path/to/myfile.json');
if (!prep.success) {
    console.error(prep.message); // e.g. "'myfile.json' is locked by another user"
    process.exit(1);
}

// ... write the file ...

const add = finishedWrite('/path/to/myfile.json');
if (!add.success) {
    console.error(add.message);
}
```

```javascript
// CommonJS
const { prepareToWrite, finishedWrite } = require('./simpleVcLib.cjs');
```

### C#
Add `SimpleVCLib.dll` to your project references.

```csharp
using SimpleVCLib;

// Before writing
var prep = VCLib.PrepareToWrite("/path/to/myfile.json");
if (!prep.Success)
{
    Console.WriteLine(prep.Message); // e.g. "'myfile.json' is locked by another user"
    return;
}

// ... write the file ...

// After writing
var add = VCLib.FinishedWrite("/path/to/myfile.json");
if (!add.Success)
    Console.WriteLine(add.Message);

// Deleting a folder
var del = VCLib.DeleteFolder("/path/to/old-content/");
if (!del.Success)
    Console.WriteLine(del.Message);
```

## Contributors
* [wildwinter](https://github.com/wildwinter) - original author

## License
```
MIT License

Copyright (c) 2026 Ian Thomas

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
```
