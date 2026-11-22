# Publishing a release

The Marketplace takes **one file**: `LiteLLM.VisualStudio.vsix`, in the repository root. The package carries
`README.md`, `LICENSE.txt` and `Resources\Icon.png`, so the listing text and the extension's own details come from the
repository rather than from the upload form. Everything below is about getting that one file right.

## 1. Version

Two places, and they must agree — the generated `extension.vsixmanifest` follows them:

- `src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj` → `<Version>`
- `src\LiteLLM.VisualStudio\source.extension.vsixmanifest` → `Identity/@Version`

Both are also what a running instance compares: Visual Studio keys an upgrade on this number. Publishing a new
package under the old number leaves the installed extension in place and loads whichever folder it finds first, so a
release that does not bump it is not a release.

## 2. Notes

Write the release section in `README.md` and the same section in `MARKETPLACE.md`, and move the previous release under
**Previously**. `README.md` is the file the extension list shows and the file the package ships, so its first section
is what a user installing this build reads.

## 3. Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-vsix.ps1 -Configuration Release
```

`tools\build-vsix.ps1` is the release script (`CONTRIBUTING.md` points at it). It builds, runs `GeneratePkgDef`, packs
the container, copies it to the repository root, and then runs `tools\verify-vsix.ps1` on that copy. Both have to
print `OK`. The older `build-vsix.ps1` in the repository root is a copy from before that script existed; do not use it.

`verify-vsix.ps1` refuses a package when:

- the version in the package, the project and `source.extension.vsixmanifest` disagree;
- the packaged assembly's file version is older than the package version, which means the container was filled from
  a stale `bin`;
- the packaged pkgdef is not the one this build's `GeneratePkgDef` wrote — the 1.8.16 defect, where the registration
  named an options page GUID the installed assembly could not answer for and `Tools → Options` showed *An error
  occurred loading this property page*;
- a required file is missing or empty, or the pkgdef names more than one options page.

It also prints the SHA-256 of the package; keep that number with the release notes if you want to be able to tell
uploaded from downloaded.

## 4. Checks that need a real Visual Studio

Nothing in the build proves a page renders. Two things do, and both are cheaper in a throwaway instance than in the
one you work in (`/RootSuffix <name>`, which gets its own `privateregistry.bin`, settings and extension folder):

1. Install it:

   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\VSIXInstaller.exe" `
       /quiet /rootSuffix:ReleaseCheck "F:\New folder (3) - Copy\LiteLLM.VisualStudio.vsix"
   ```

   Every `devenv.exe` must be closed first: `VSIXInstaller` exits **2004** while any instance is running.

2. Start that instance and open **Tools → Options → LiteLLM → General**. It must draw its settings, not the error
   pane. A fresh suffix does not merge the new pkgdef until its `Extensions\extensions.configurationchanged` marker is
   newer than the installed folder, so touch that marker before the start if the category is missing entirely. The
   category missing is a merge problem; the category present with an error pane is a registration problem.

## 5. Upload

Without a personal access token, upload in the browser: <https://marketplace.visualstudio.com/manage> → the
extension → **Update** → the `.vsix` → confirm the version shown matches step 1 → publish.

With one, from a Visual Studio developer prompt:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe" `
    login -personalAccessToken <token> -publisherName EsmaeilHatami
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe" `
    publish -payload "F:\New folder (3) - Copy\LiteLLM.VisualStudio.vsix" -manifest publish-manifest.json
```

`publish-manifest.json` is not in this repository: `VsixPublisher` replaces the listing's categories and overview from
it, so it has to name the categories the listing already uses and point `overview` at `MARKETPLACE.md`. Uploading in
the browser keeps whatever the listing already says and only moves the package forward, which is why it is the
default here.

## 6. After publishing

- The listing shows the new version, and its date is today rather than the date in the notes.
- Download the package the listing serves and run `tools\verify-vsix.ps1` on it; its SHA-256 must be the one step 3
  printed. A mismatch means the Marketplace served a different file from the one that was checked.
- Install once from the listing, in a suffix you do not work in, and open `Tools → Options → LiteLLM → General` — the
  same check as step 4, on the package users actually get.
