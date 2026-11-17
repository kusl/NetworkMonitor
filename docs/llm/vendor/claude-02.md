00
65

All the builds are failing 

build (ubuntu-latest, linux-x64, network-monitor-linux-x64, tar)
Process completed with exit code 1.
build (ubuntu-latest, linux-arm64, network-monitor-linux-arm64, tar)
Process completed with exit code 1.
build (macos-latest, osx-x64, network-monitor-osx-x64, tar)
Process completed with exit code 1.
build (macos-latest, osx-arm64, network-monitor-osx-arm64, tar)
Process completed with exit code 1.
build (windows-latest, win-arm64, network-monitor-win-arm64, zip)
Process completed with exit code 1.
build (windows-latest, win-x64, network-monitor-win-x64, zip)
Process completed with exit code 1.

Now let me look at the workflow step and the repo state.Root cause confirmed. Writing the fixed workflow.The workflow sets an environment variable called `VERSION` for the publish step, and that's what breaks all six builds. MSBuild turns every environment variable into a build property, so `VERSION=v1.0.20261117.135453` silently sets the project's `$(Version)`.

The diag log shows it in the restore spec for both projects:

```
Version=v1.0.20261117.135453
```

The leading `v` makes that an invalid package version. NuGet in SDK 11.0.100 gives up on it in about 26 ms, before it downloads anything, and never logs a NuGet error code. That's why the only error is the vague `MSB4181 "RestoreTask" returned false but did not log an error`.

This also explains why the `test` job passes: it has no `VERSION` variable. The variable came in with the .NET 11 workflow rewrite (`ac7d078`).

**The fix:** I renamed `VERSION` to `RELEASE_VERSION` everywhere in the workflow. Your `-p:InformationalVersion` still gets the version string, but `$(Version)` is no longer overridden. I couldn't run a build here because the .NET and NuGet download servers are blocked from this session, so the next push is the real test. I also removed `-v:diag`, which was what made the log 6 MB. I kept the binlog upload, which only runs when a build fails.

No other files change, and nothing needs deleting.

**Full file path:** `.github/workflows/release.yml`

The complete file is attached. The only changes from the current version are:

- Line 33–35: `VERSION` → `RELEASE_VERSION` (version job)
- Line 119: `env: RELEASE_VERSION: ${{ needs.version.outputs.version }}`
- Line 128: `-p:InformationalVersion="${RELEASE_VERSION#v}"`, and the `-v:diag` line is gone
- Line 171: release job `env: RELEASE_VERSION: ...`
- Lines 247–248, 262, 264: `$VERSION` → `$RELEASE_VERSION` in `gh release create` and the summary

Don't fix this by passing `-p:Version=1.0.20261117.135453` instead. `20261117` is larger than 65535, the maximum for one part of an assembly version, so that would fail the build in a different way.

03
66




















































I don't think you have identified the root cause. 
here is the original failure that caused me to add the diagnostic code 
https://github.com/kusl/NetworkMonitor/actions/runs/44031596065/job/133864779840
https://productionresultssa16.blob.core.windows.net/actions-results/f1db6525-5106-8838-8b6f-320c1be4b16c/workflow-job-run-ca044659-5ffe-5768-9e5c-845f61e6062a/logs/job/job-logs.txt?rsct=text%2Fplain&se=2026-11-17T14%3A40%3A29Z&sig=SISU9gY0qkPSq5U769TmsDKnt6E5X2X5Io2BbuI6Hbs%3D&ske=2026-11-17T15%3A42%3A16Z&skoid=ca7593d4-ee42-46cd-af88-8b886a2f84eb&sks=b&skt=2026-11-17T11%3A42%3A16Z&sktid=398a6654-997b-47e9-b12b-9515b896b4de&skv=2025-11-05&sp=r&spr=https&sr=b&st=2026-11-17T14%3A30%3A24Z&sv=2025-11-05
Please review and give me the full file 
please remove the diagnostic code as well 

Reproduced it. Now writing the final file without the diagnostic steps.The original failure has the same cause. Line 234 of that log shows the publish step running with `VERSION: v1.0.20261117.062937` in its environment, the same as the diagnostic run. I couldn't open the blob link from here, but the file you uploaded is that same job's log.

**What I checked:**

- **Reproduced locally.** I used SDK 10.0.115 because 11.0.100 can't be downloaded here. A project with no packages, no RID and no global.json:
  ```
  no VERSION                       → Restored (in 112 ms)
  VERSION=v1.0.20261117.062937     → error : 'v1.0.20261117.062937' is not a valid version string. (Parameter 'value')
  VERSION=1.0.20261117.062937      → Restored (in 102 ms)
  ```
  MSBuild reads environment variables as properties, and property names ignore case, so `VERSION` overrides `$(Version)`. SDK 10 at least prints the reason. SDK 11.0.100 hits the same parse failure and drops the message, which leaves only `MSB4181 ... did not log an error`. That fits your failures: both projects fail in about 25 ms and nothing is downloaded.
- **Why it only started now.** Before `ac7d078`, the workflow only used the version in the release job, as `steps.version.outputs.version`. It never reached `dotnet publish`. `ac7d078` added `env: VERSION:` to the publish step, and the first push after it failed.
- **Why `test` passes.** It uses the same SDK, projects and runner, but it has no `VERSION` variable.

**Fix:** rename the variable to `RELEASE_VERSION` everywhere in the workflow. The diagnostic code (`-v:diag`, `-bl:`, and the binlog upload step) is removed. Compared with the workflow before the diagnostics, the rename is the only change. Nothing else changes and no files need deleting.

**Path:** `.github/workflows/release.yml`

```yaml
name: Build and Release

on:
  push:
    branches:
      - '**'

permissions:
  contents: write

concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

env:
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CHANNEL: '11.0'
  PROJECT_PATH: src/NetworkMonitor.Console/NetworkMonitor.Console.csproj
  SOLUTION_PATH: src/NetworkMonitor.slnx

jobs:
  version:
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.version.outputs.version }}
    steps:
      - name: Generate version string
        id: version
        shell: bash
        run: |
          RELEASE_VERSION="v1.0.$(date -u +%Y%m%d.%H%M%S)"
          echo "version=${RELEASE_VERSION}" >> "$GITHUB_OUTPUT"
          echo "Generated version: ${RELEASE_VERSION}"

  test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repository
        uses: actions/checkout@v7

      - name: Install .NET
        shell: bash
        run: |
          curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
          chmod +x dotnet-install.sh
          ./dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet"
          echo "$HOME/.dotnet" >> "$GITHUB_PATH"
          echo "DOTNET_ROOT=$HOME/.dotnet" >> "$GITHUB_ENV"

      - name: Test
        shell: bash
        run: dotnet test --solution "$SOLUTION_PATH" --configuration Release

  build:
    needs: [version, test]
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: windows-latest
            rid: win-x64
            artifact-name: network-monitor-win-x64
            archive-type: zip
          - os: windows-latest
            rid: win-arm64
            artifact-name: network-monitor-win-arm64
            archive-type: zip
          - os: macos-latest
            rid: osx-x64
            artifact-name: network-monitor-osx-x64
            archive-type: tar
          - os: macos-latest
            rid: osx-arm64
            artifact-name: network-monitor-osx-arm64
            archive-type: tar
          - os: ubuntu-latest
            rid: linux-x64
            artifact-name: network-monitor-linux-x64
            archive-type: tar
          - os: ubuntu-latest
            rid: linux-arm64
            artifact-name: network-monitor-linux-arm64
            archive-type: tar

    runs-on: ${{ matrix.os }}

    steps:
      - name: Checkout repository
        uses: actions/checkout@v7

      - name: Install .NET (Unix)
        if: runner.os != 'Windows'
        shell: bash
        run: |
          curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
          chmod +x dotnet-install.sh
          ./dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet"
          echo "$HOME/.dotnet" >> "$GITHUB_PATH"
          echo "DOTNET_ROOT=$HOME/.dotnet" >> "$GITHUB_ENV"

      - name: Install .NET (Windows)
        if: runner.os == 'Windows'
        shell: pwsh
        run: |
          Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
          ./dotnet-install.ps1 -Channel $env:DOTNET_CHANNEL -InstallDir "$env:USERPROFILE\.dotnet"
          echo "$env:USERPROFILE\.dotnet" | Out-File -FilePath $env:GITHUB_PATH -Append
          echo "DOTNET_ROOT=$env:USERPROFILE\.dotnet" | Out-File -FilePath $env:GITHUB_ENV -Append

      - name: Verify .NET installation
        shell: bash
        run: dotnet --info

      - name: Publish self-contained binary
        shell: bash
        env:
          RELEASE_VERSION: ${{ needs.version.outputs.version }}
        run: |
          dotnet publish "$PROJECT_PATH" \
            --configuration Release \
            --runtime ${{ matrix.rid }} \
            --self-contained true \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:DebugType=None \
            -p:DebugSymbols=false \
            -p:InformationalVersion="${RELEASE_VERSION#v}" \
            --output ./publish/${{ matrix.rid }}
          ls -la ./publish/${{ matrix.rid }}/

      - name: Create tar.gz archive (Unix)
        if: matrix.archive-type == 'tar'
        shell: bash
        run: |
          tar -C publish -czvf ${{ matrix.artifact-name }}.tar.gz ${{ matrix.rid }}
          ls -la ${{ matrix.artifact-name }}.tar.gz

      - name: Create zip archive (Windows)
        if: matrix.archive-type == 'zip'
        shell: pwsh
        run: |
          Compress-Archive -Path "publish\${{ matrix.rid }}" -DestinationPath "${{ matrix.artifact-name }}.zip"
          Get-ChildItem "${{ matrix.artifact-name }}.zip"

      - name: Upload build artifact
        uses: actions/upload-artifact@v7
        with:
          name: ${{ matrix.artifact-name }}
          path: |
            ${{ matrix.artifact-name }}.tar.gz
            ${{ matrix.artifact-name }}.zip
          retention-days: 1
          if-no-files-found: error

  release:
    needs: [version, build]
    runs-on: ubuntu-latest
    env:
      RELEASE_VERSION: ${{ needs.version.outputs.version }}

    steps:
      - name: Checkout repository
        uses: actions/checkout@v7
        with:
          fetch-depth: 50

      - name: Download all build artifacts
        uses: actions/download-artifact@v8
        with:
          path: ./artifacts

      - name: Prepare release assets
        shell: bash
        run: |
          mkdir -p ./release-assets
          find ./artifacts -type f \( -name "*.zip" -o -name "*.tar.gz" \) -exec mv {} ./release-assets/ \;
          cd ./release-assets
          sha256sum * > SHA256SUMS.txt
          cat SHA256SUMS.txt

      - name: Generate release notes
        shell: bash
        run: |
          {
            echo "## Network Monitor Release"
            echo ""
            echo "**Branch:** \`${{ github.ref_name }}\`"
            echo "**Commit:** \`${{ github.sha }}\`"
            echo ""
            echo "### Commit"
            echo ""
            echo '```'
            git log -1 --pretty=format:'%h  %an  %ai%n%n%s%n%n%b'
            echo ""
            echo '```'
            echo ""
            echo "### Downloads"
            echo ""
            echo "| Platform | Architecture | Filename |"
            echo "|----------|--------------|----------|"
            echo "| Windows | x64 | \`network-monitor-win-x64.zip\` |"
            echo "| Windows | ARM64 | \`network-monitor-win-arm64.zip\` |"
            echo "| macOS | x64 (Intel) | \`network-monitor-osx-x64.tar.gz\` |"
            echo "| macOS | ARM64 (Apple Silicon) | \`network-monitor-osx-arm64.tar.gz\` |"
            echo "| Linux | x64 | \`network-monitor-linux-x64.tar.gz\` |"
            echo "| Linux | ARM64 | \`network-monitor-linux-arm64.tar.gz\` |"
            echo ""
            echo "### Installation"
            echo ""
            echo "**Windows:**"
            echo '```powershell'
            echo "Expand-Archive network-monitor-win-x64.zip -DestinationPath ."
            echo ".\\win-x64\\NetworkMonitor.Console.exe"
            echo '```'
            echo ""
            echo "**macOS / Linux:**"
            echo '```bash'
            echo "tar -xzf network-monitor-linux-x64.tar.gz"
            echo "./linux-x64/NetworkMonitor.Console"
            echo '```'
            echo ""
            echo "### SHA256 Checksums"
            echo ""
            echo '```'
            cat ./release-assets/SHA256SUMS.txt
            echo '```'
          } > release-notes.md
          cat release-notes.md

      - name: Create GitHub Release
        shell: bash
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          gh release create "$RELEASE_VERSION" \
            --title "Release $RELEASE_VERSION" \
            --notes-file release-notes.md \
            --target "${{ github.sha }}" \
            --latest \
            ./release-assets/*.zip \
            ./release-assets/*.tar.gz \
            ./release-assets/SHA256SUMS.txt

      - name: Output release summary
        shell: bash
        run: |
          {
            echo "## ✅ Release Created Successfully"
            echo ""
            echo "**Version:** \`$RELEASE_VERSION\`"
            echo ""
            echo "**Release URL:** https://github.com/${{ github.repository }}/releases/tag/$RELEASE_VERSION"
            echo ""
            echo "### Assets"
          } >> "$GITHUB_STEP_SUMMARY"
          for file in ./release-assets/*; do
            echo "- \`$(basename "$file")\` ($(ls -lh "$file" | awk '{print $5}'))" >> "$GITHUB_STEP_SUMMARY"
          done
```

I couldn't run SDK 11.0.100 here, so the next push is the real test. If the build still fails after this change, send me that log.

04
66








