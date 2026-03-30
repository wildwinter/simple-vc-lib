# simple-vc-lib
**simple-vc-lib** is a multi-language library that provides an agnostic wrapper around common version control systems for tools. It lets your tools create, edit, and delete files without needing to know or care which version control system the user has in place.

I wrote this for game dev tooling, but it might be useful for your other projects.

```javascript
import { writeTextFile, deleteFile } from './simpleVcLib.js';

// Write a file - checks out if needed, writes, adds to VC if new
const result = writeTextFile('/path/to/dialogue.json', JSON.stringify(data));
if (!result.success) {
    console.error(result.message); // e.g. "File is locked by another user"
    process.exit(1);
}

// Deleting - marks for deletion in VC if tracked
deleteFile('/path/to/old-dialogue.json');
```

```csharp
using SimpleVCLib;

// Write a file - checks out if needed, writes, adds to VC if new
var result = VCLib.WriteTextFile("/path/to/dialogue.json", jsonContent);
if (!result.Success) {
    Console.WriteLine(result.Message); // e.g. "File is locked by another user"
    return;
}

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
        * [writeTextFile](#writetextfilepath-content-encoding)
        * [writeBinaryFile](#writebinaryfilepath-data)
        * [prepareToWrite](#preparetowritefilepath)
        * [finishedWrite](#finishedwritefilepath)
        * [deleteFile](#deletefilefilepath)
        * [deleteFolder](#deletefolderfolderpath)
        * [renameFile](#renamefileoldpath-newpath)
        * [renameFolder](#renamefolderoldpath-newpath)
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
* **Javascript** — available on [npm](https://www.npmjs.com/package/@wildwinter/simple-vc-lib) as `@wildwinter/simple-vc-lib`. Includes ESM and CommonJS builds with TypeScript definitions.
* **C#** — available on [NuGet](https://www.nuget.org/packages/wildwinter.SimpleVCLib) as `wildwinter.SimpleVCLib`. Targets .NET 8.

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

**NOTE:** I haven't been able to test Plastic SCM fully yet. Please let me know if you have any issues with it.

## Usage

### Overview
* Use **`writeTextFile`** or **`writeBinaryFile`** to write a file. These all-in-one helpers check out or unlock the file if needed, write it, and add it to VC if it's new. Works whether or not the file already exists.
* If you need finer control, the steps are also available individually: call **`prepareToWrite`** before writing (checks out / unlocks the file, or no-ops if it doesn't exist yet), then write the file yourself, then call **`finishedWrite`** afterwards (adds the file to VC if it's new).
* Call **`deleteFile`** or **`deleteFolder`** to remove files. Tracked files will be marked for deletion in the VC system; untracked files are just deleted from disk.

You don't need to tell the library which VC system is in use — it detects this automatically. See [VC Detection](#vc-detection) below.

### Operations

#### `writeTextFile(filePath, content, encoding)`
An all-in-one helper that calls `prepareToWrite`, writes `content` as text, then calls `finishedWrite`. Works whether or not the file already exists.

- `encoding` defaults to UTF-8 (without BOM).
- Returns the result from whichever step failed, or the result of `finishedWrite` on success.

#### `writeBinaryFile(filePath, data)`
An all-in-one helper that calls `prepareToWrite`, writes `data` as raw bytes, then calls `finishedWrite`. Works whether or not the file already exists.

- Returns the result from whichever step failed, or the result of `finishedWrite` on success.

#### `prepareToWrite(filePath)`
Prepares a file path for writing. Use this when you need to write the file yourself rather than via `writeTextFile` / `writeBinaryFile`.
- If the file does not yet exist: succeeds immediately (ready to create).
- If the file exists and is writable: succeeds immediately.
- If the file exists and is read-only: checks it out (Perforce, Plastic SCM, SVN) or removes the read-only attribute (Git, filesystem).

On failure, the `status` field indicates the reason: `locked` means the file is exclusively held by another user; `outOfDate` means the local copy is behind the depot and needs syncing first.

#### `finishedWrite(filePath)`
Notifies the library that a file has been written. Use this after writing the file yourself following a `prepareToWrite` call.
- If the file is newly created (not yet in VC): adds it (`git add`, `p4 add`, `cm add`, `svn add`).
- If the file was already tracked: no-op.

#### `deleteFile(filePath)`
Deletes a file, scheduling it for deletion in VC if it is tracked.

#### `deleteFolder(folderPath)`
Deletes a folder and all its contents. Tracked files are scheduled for VC deletion; untracked files are deleted from disk.

#### `renameFile(oldPath, newPath)`
Renames (moves) a file, informing VC of the change if the file is tracked (`git mv`, `p4 move`, `cm mv`, `svn move`). Untracked files are renamed on disk only. No-op if the source does not exist.

#### `renameFolder(oldPath, newPath)`
Renames (moves) a folder, informing VC of the change for all tracked contents. Untracked content is moved on disk. No-op if the source does not exist.

### Return Values
All operations return a result object with three fields:

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

Detection results are cached by VCS root directory. After the first operation on a file inside a repo, subsequent operations on files in the same repo skip the directory walk entirely. Files outside any known VCS root (for example, writing to a temp directory) are detected independently and will use the filesystem fallback without affecting the cache.

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
Install via npm:
```bash
npm install @wildwinter/simple-vc-lib
```

Or download `simpleVcLib.js` (ESM) or `simpleVcLib.cjs` (CommonJS) from the [GitHub releases area](https://github.com/wildwinter/simple-vc-lib/releases) and add them directly to your project.

```javascript
// ESM (npm)
import { writeTextFile, writeBinaryFile, prepareToWrite, finishedWrite, deleteFile, deleteFolder, renameFile, renameFolder } from '@wildwinter/simple-vc-lib';

// ESM (direct file)
import { writeTextFile, writeBinaryFile, prepareToWrite, finishedWrite, deleteFile, deleteFolder, renameFile, renameFolder } from './simpleVcLib.js';

// All-in-one helpers (checkout + write + add to VC)
const result = writeTextFile('/path/to/myfile.json', JSON.stringify(data), 'utf8');
if (!result.success) {
    console.error(result.message); // e.g. "'myfile.json' is locked by another user"
    process.exit(1);
}

const binResult = writeBinaryFile('/path/to/myfile.bin', buffer);
if (!binResult.success) {
    console.error(binResult.message);
}

// Renaming
const renResult = renameFile('/path/to/old-name.json', '/path/to/new-name.json');
if (!renResult.success) {
    console.error(renResult.message);
}

renameFolder('/path/to/old-folder', '/path/to/new-folder');

// Manual approach — if you need to write the file yourself
const prep = prepareToWrite('/path/to/myfile.json');
if (!prep.success) {
    console.error(prep.message);
    process.exit(1);
}
// ... write the file ...
const add = finishedWrite('/path/to/myfile.json');
if (!add.success) {
    console.error(add.message);
}
```

```javascript
// CommonJS (npm)
const { writeTextFile, writeBinaryFile, prepareToWrite, finishedWrite } = require('@wildwinter/simple-vc-lib');

// CommonJS (direct file)
const { writeTextFile, writeBinaryFile, prepareToWrite, finishedWrite } = require('./simpleVcLib.cjs');
```

### C#
Install via NuGet:
```bash
dotnet add package wildwinter.SimpleVCLib
```

Or download `SimpleVCLib.dll` from the [GitHub releases area](https://github.com/wildwinter/simple-vc-lib/releases) and add it to your project references directly.

```csharp
using SimpleVCLib;

// All-in-one helpers (checkout + write + add to VC)
var result = VCLib.WriteTextFile("/path/to/myfile.json", jsonContent);
if (!result.Success)
    Console.WriteLine(result.Message); // e.g. "'myfile.json' is locked by another user"

// With explicit encoding
var result2 = VCLib.WriteTextFile("/path/to/myfile.txt", text, System.Text.Encoding.Unicode);
if (!result2.Success)
    Console.WriteLine(result2.Message);

var binResult = VCLib.WriteBinaryFile("/path/to/myfile.bin", data);
if (!binResult.Success)
    Console.WriteLine(binResult.Message);

// Manual approach — if you need to write the file yourself
var prep = VCLib.PrepareToWrite("/path/to/myfile.json");
if (!prep.Success)
{
    Console.WriteLine(prep.Message);
    return;
}
// ... write the file ...
var add = VCLib.FinishedWrite("/path/to/myfile.json");
if (!add.Success)
    Console.WriteLine(add.Message);

// Deleting a folder
var del = VCLib.DeleteFolder("/path/to/old-content/");
if (!del.Success)
    Console.WriteLine(del.Message);

// Renaming
var ren = VCLib.RenameFile("/path/to/old-name.json", "/path/to/new-name.json");
if (!ren.Success)
    Console.WriteLine(ren.Message);

VCLib.RenameFolder("/path/to/old-folder", "/path/to/new-folder");
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
