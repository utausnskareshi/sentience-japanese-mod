<#
.SYNOPSIS
    パッチツールを配布用の単一 exe（.NET ランタイム同梱・ペイロード埋め込み）としてビルドする。

.DESCRIPTION
    0. 引数を検査する。-RuntimeIdentifier は英数字・"."・"-" だけ（".." や区切り文字で作業用フォルダの外を消さないため）。
       出力先・ペイロードのパスは、使えない文字（PowerShell 5.1 から末尾 "\" の引用符付きパスを渡したときに混ざる '"' など）が無いことを、
       リポジトリ・ペイロード・出力先・作業用フォルダのパスは "," ";"（MSBuild がプロパティの値を区切る文字）が無いことを、ビルドの前に確かめる。
    1. ペイロード（TypeScript 側が生成する dist/payload/manifest.json 等）の存在を確認し、patchVersion を読み取る。無ければ分かりやすく失敗する。
       ペイロードフォルダの実ファイルが「manifest.json ＋ manifest の files に載ったファイル（game/ 配下）」とちょうど一致することも確かめる
       （余分なファイルは exe に黙って埋め込まれてしまうため）。出力先のフォルダもここで作っておく。
    2. テストを実行する（-SkipTests で省略可能）。dotnet はリポジトリ直下で起動し、global.json の SDK 指定を必ず効かせる。
    3. dotnet publish で self-contained・単一ファイル・圧縮ありの exe を、毎回空にする作業用フォルダ（patcher/obj/publish-staging）に作る。
       publish の前にパッチツール本体の Release の中間出力（obj・bin）を消し、指定したペイロードを必ず埋め込み直す
       （同じフォルダの中身を更新日時の古い別の内容へ差し替えると、増分ビルドが前回の埋め込みのまま exe を作ってしまうため）。
       exe のバージョンには patchVersion を使い、出力が exe 1 つだけであることを確認する。
    4. できた exe を自己検査モード（--verify-payload）で起動し、埋め込んだペイロードを読み込めることを終了コードで確認する。
       さらに、自己検査が報告する「埋め込んだ manifest.json の SHA-256」が、指定したペイロードの manifest.json と一致することを確かめる。
    5. 確認できた exe を出力先へコピーし、サイズと SHA-256 を表示する（配布時にハッシュを公開するため）。

.PARAMETER PayloadDirectory
    埋め込むペイロードのフォルダ。省略時はリポジトリ直下の dist/payload。相対パスは PowerShell の現在の場所を基準にする。

.PARAMETER OutputDirectory
    exe の出力先。省略時はリポジトリ直下の dist/publish。相対パスは PowerShell の現在の場所を基準にする。
    出力先の既存のファイルは削除しない（exe だけを置き換え、他のファイルがあれば警告する）。

.PARAMETER RuntimeIdentifier
    対象のランタイム識別子。既定は win-x64。英数字で始まり、英数字・"."・"-" だけからなる値のみ受け付ける。

.PARAMETER SkipTests
    テストを省略する。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File patcher\build.ps1
#>
[CmdletBinding()]
param(
    [string]$PayloadDirectory,
    [string]$OutputDirectory,

    # ".." や区切り文字を含む値で、作業用フォルダ以外を削除しないよう、使える文字を限る（AA-02）
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9.-]*\z')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 出力する exe の名前（プロジェクトの AssemblyName と一致させる）
$exeName = 'SentienceJP-Patcher.exe'

# 自己検査（exe の起動）を待つ上限の秒数。画面が出てしまった場合などに、ビルドが止まり続けないようにする。
$selfCheckTimeoutSeconds = 120

# パスを PowerShell の現在の場所（Set-Location した場所）を基準にフルパスへ変換し、末尾の区切り文字を取り除く。
# - [System.IO.Path]::GetFullPath はプロセスの現在位置を基準にするため、PowerShell 上で移動した場所とずれることがある。
# - 末尾が "\" のまま空白を含むパスを dotnet に渡すと、閉じ引用符がエスケープされて引数が壊れる。
function Resolve-FullPath([string]$Path) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $trimmed = $full.TrimEnd('\', '/')

    # ドライブのルート（例: "D:\"）は区切りを残す（"D:" だけではそのドライブの現在位置を指してしまうため）
    if ($trimmed.EndsWith(':')) { return $trimmed + '\' }
    return $trimmed
}

# 引数のパスを検査してからフルパスにする（AA-14）。ビルドを始める前に、分かりやすいメッセージで止めるため。
# - '"' を含む: PowerShell 5.1 から -File で、末尾が "\" の引用符付きのパス（例: '.\out dir\'）を渡すと、閉じ引用符が値に含まれてしまう。
# - その他の使えない文字・パスとして解釈できない値も、ここで止める。
function Resolve-UsablePath([string]$Path, [string]$Label) {
    if ($Path.Contains('"')) {
        throw ("{0}のパスに '""' が含まれています: {1}`n" -f $Label, $Path) +
            'PowerShell 5.1 から -File で、末尾が "\" の引用符付きのパス（例: ''.\out dir\''）を渡すと、閉じ引用符が値に含まれてしまいます。末尾の \ を外して指定してください。'
    }

    if ($Path.IndexOfAny([System.IO.Path]::GetInvalidPathChars()) -ge 0) {
        throw ("{0}のパスに使えない文字が含まれています: {1}" -f $Label, $Path)
    }

    try {
        $full = Resolve-FullPath $Path
        $null = [System.IO.Path]::GetFullPath($full)
    }
    catch {
        throw ("{0}のパスが不正です（{1}）: {2}" -f $Label, $_.Exception.Message, $Path)
    }

    return $full
}

# パスに MSBuild がプロパティの値を区切る文字（"," と ";"）が含まれていれば、ビルドの前に止める（AA-06）。
# dotnet にプロパティとして渡すと値が分割され、"MSB1006: プロパティが無効です" で失敗するため。
function Assert-NoMSBuildSeparator([string]$Path, [string]$Label) {
    if ($Path.IndexOfAny([char[]]@(',', ';')) -ge 0) {
        throw ("{0}のパスに "","" または "";"" が含まれているため、ビルドできません: {1}`n" -f $Label, $Path) +
            'MSBuild はコマンドラインで渡したプロパティの値をこれらの文字で区切るため、パスとして正しく渡せません。これらの文字を含まない場所へ移動（またはコピー）してから、もう一度実行してください。'
    }
}

$patcherRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $patcherRoot
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $repoRoot 'dist\payload' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'dist\publish' }

# 相対パスの引数は、dotnet を呼ぶ前のリポジトリへの移動（AA-05）より前に、PowerShell の現在の場所を基準に解決する
$PayloadDirectory = Resolve-UsablePath $PayloadDirectory 'ペイロード（-PayloadDirectory）'
$OutputDirectory = Resolve-UsablePath $OutputDirectory '出力先（-OutputDirectory）'

# 作業用フォルダは patcher\obj\publish-staging\ の配下でなければならない（削除する前に確かめる。AA-02）
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $patcherRoot 'obj\publish-staging')).TrimEnd('\') + '\'
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $stagingRoot $RuntimeIdentifier))
if (-not $stagingDirectory.StartsWith($stagingRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $stagingDirectory.Length -le $stagingRoot.Length) {
    throw "作業用フォルダが想定の場所（$stagingRoot）の配下にありません: $stagingDirectory（-RuntimeIdentifier の値を確認してください）"
}

Assert-NoMSBuildSeparator $repoRoot 'リポジトリ'
Assert-NoMSBuildSeparator $PayloadDirectory 'ペイロード（-PayloadDirectory）'
Assert-NoMSBuildSeparator $OutputDirectory '出力先（-OutputDirectory）'
Assert-NoMSBuildSeparator $stagingDirectory '作業用フォルダ'

# 1. ペイロードの確認と、パッチのバージョンの読み取り（無ければ以降の処理をせずに失敗させる）
$manifestPath = Join-Path $PayloadDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "ペイロードが見つかりません: $manifestPath`nリポジトリ直下で TypeScript 側のペイロード生成を実行してから、再度このスクリプトを実行してください。"
}

# 埋め込むべき manifest.json の SHA-256（自己検査が報告する、exe に埋め込まれた manifest.json の SHA-256 と最後に照合する。AA-01）
$expectedManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$patchVersionProperty = $manifest.PSObject.Properties['patchVersion']
if ($null -eq $patchVersionProperty -or -not ($patchVersionProperty.Value -is [string])) {
    throw "manifest.json に文字列の patchVersion がありません: $manifestPath"
}

# exe のバージョン（-p:Version）として使える形式か確認する（例: 0.1.0、1.2.3-beta.1）。
# 末尾の改行や全角・アラビア数字を通さないよう、\A・\z と [0-9] を使い、大文字小文字も区別する（AA-15。C# の PatchVersionFormat と同じ形式）
$patchVersion = $patchVersionProperty.Value
if ($patchVersion -cnotmatch '\A[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z') {
    throw "manifest.json の patchVersion が exe のバージョンとして使えない形式です: '$patchVersion'（$manifestPath）"
}
Write-Host "パッチのバージョン: $patchVersion"

# ペイロードフォルダの実ファイルの集合が「manifest.json ＋ files の各 path（game/ 配下）」と一致することを確かめる（AA-03）。
# manifest に無いファイル（エディタのバックアップ・メモ・古いビルドの残り）も exe に埋め込まれてしまうため、余分も不足も失敗にする。
$filesProperty = $manifest.PSObject.Properties['files']
if ($null -eq $filesProperty -or $null -eq $filesProperty.Value) {
    throw "manifest.json に files がありません: $manifestPath"
}

$expectedFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
[void]$expectedFiles.Add('manifest.json')
foreach ($fileEntry in @($filesProperty.Value)) {
    $pathProperty = $fileEntry.PSObject.Properties['path']
    if ($null -eq $pathProperty -or -not ($pathProperty.Value -is [string])) {
        throw "manifest.json の files に、文字列の path が無い要素があります: $manifestPath"
    }
    [void]$expectedFiles.Add('game/' + $pathProperty.Value)
}

$payloadPrefixLength = $PayloadDirectory.TrimEnd('\').Length
$actualFiles = @(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Force -File |
    ForEach-Object { $_.FullName.Substring($payloadPrefixLength).TrimStart('\').Replace('\', '/') })
$actualSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $actualFiles) { [void]$actualSet.Add($relative) }
$extraFiles = @($actualFiles | Where-Object { -not $expectedFiles.Contains($_) } | Sort-Object)
$missingFiles = @($expectedFiles | Where-Object { -not $actualSet.Contains($_) } | Sort-Object)
if ($extraFiles.Count -gt 0 -or $missingFiles.Count -gt 0) {
    $details = @()
    if ($extraFiles.Count -gt 0) { $details += '余分なファイル（manifest.json に無く、exe に埋め込まれてしまう）: ' + ($extraFiles -join ', ') }
    if ($missingFiles.Count -gt 0) { $details += '不足しているファイル（manifest.json にあるが見つからない）: ' + ($missingFiles -join ', ') }
    throw ("ペイロードフォルダのファイルが manifest.json と一致しません（{0}）。`n{1}`n" -f $PayloadDirectory, ($details -join "`n")) +
        'TypeScript 側のペイロード生成をやり直すか、余分なファイルを取り除いてから、もう一度実行してください。'
}

# 出力先のフォルダは、時間のかかるビルドの前に作っておく（作れない場所なら、ここで分かりやすく止める。AA-14）
try {
    [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
}
catch {
    throw "出力先のフォルダを作れません（$($_.Exception.Message)）: $OutputDirectory"
}

# dotnet はプロセスの作業フォルダから上へ global.json を探すため、リポジトリ直下へ移動してから呼ぶ（AA-05）。
# どこから実行しても global.json の SDK でビルドされるようにする。終了時（失敗時も）に元の場所へ戻す。
Push-Location -LiteralPath $repoRoot
try {
    # 2. テスト（Core の単体・結合テストと、画面の ViewModel のテスト）。
    #    画面のプロジェクトは Release ではペイロードが必須のため、指定したペイロードを渡す。
    if (-not $SkipTests) {
        Write-Host 'テストを実行しています…'
        & dotnet test (Join-Path $patcherRoot 'SentienceJP.Patcher.slnx') -c Release "-p:PayloadDirectory=$PayloadDirectory"
        if ($LASTEXITCODE -ne 0) { throw "テストが失敗しました（終了コード $LASTEXITCODE）。" }
    }

    # 3. 単一 exe として publish（.NET ランタイム同梱・圧縮あり・ネイティブライブラリも同梱）。
    #    毎回空にする作業用フォルダへ出力し、exe 1 つだけであることと自己検査の合格を確かめてから出力先へコピーする
    #    （以前のビルドのファイルが混ざったり、検査前・検査不合格の exe が出力先に置かれたりしないようにするため）。
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    # パッチツール本体の Release の中間出力・出力を消して、ペイロードを必ず埋め込み直す（AA-01）。
    # MSBuild は「入力の更新日時」と「入力パスの一覧」でしか再コンパイルを判断しないため、同じフォルダの中身を更新日時の古い別の内容へ
    # 差し替えると、古い埋め込みのまま exe を作ってしまう。テストで使った Core 等の出力は残すので、消すのは本体の分だけ（所要時間への影響は小さい）。
    foreach ($intermediate in @('obj\Release', 'bin\Release')) {
        $intermediatePath = Join-Path $patcherRoot ('src\SentienceJP.Patcher\' + $intermediate)
        if (Test-Path -LiteralPath $intermediatePath) {
            Remove-Item -LiteralPath $intermediatePath -Recurse -Force
        }
    }

    Write-Host "publish しています（ペイロード: $PayloadDirectory）…"
    $project = Join-Path $patcherRoot 'src\SentienceJP.Patcher\SentienceJP.Patcher.csproj'
    & dotnet publish $project `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:RequirePayload=true `
        "-p:PayloadDirectory=$PayloadDirectory" `
        "-p:Version=$patchVersion" `
        -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗しました（終了コード $LASTEXITCODE）。" }

    $unexpectedFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -Force -File | Where-Object { $_.Name -ne $exeName })
    if ($unexpectedFiles.Count -gt 0) {
        throw ('単一 exe 以外のファイルが出力されました（同梱の設定を確認してください）: ' + (($unexpectedFiles | ForEach-Object { $_.FullName }) -join ', '))
    }

    $stagedExe = Join-Path $stagingDirectory $exeName
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "出力された exe が見つかりません: $stagedExe"
    }

    # 4. 自己検査: できた exe を画面なしで起動し、埋め込んだペイロードを読み込めるかを終了コードで確認する。
    #    WPF の exe（コンソールを持たない）のため、Start-Process で起動して終了コードを受け取り、結果の説明はファイルで受け取る。
    Write-Host '埋め込みペイロードを自己検査しています…'
    $reportPath = Join-Path (Split-Path -Parent $stagingDirectory) "selfcheck-$RuntimeIdentifier.txt"
    if (Test-Path -LiteralPath $reportPath) {
        Remove-Item -LiteralPath $reportPath -Force
    }

    $process = Start-Process -FilePath $stagedExe -ArgumentList ('--verify-payload "{0}"' -f $reportPath) -PassThru

    # Windows PowerShell 5.1 では、終了前にハンドルを取得しておかないと終了コードを取れないことがあるため、先に取得しておく
    $null = $process.Handle
    if (-not $process.WaitForExit($selfCheckTimeoutSeconds * 1000)) {
        try {
            $process.Kill()
        }
        catch {
            Write-Warning "自己検査の exe を終了できませんでした（タスク マネージャーで終了してください）: $_"
        }
        throw "自己検査が $selfCheckTimeoutSeconds 秒以内に終わりませんでした（画面が表示されて止まっている可能性があります）: $stagedExe"
    }

    $exitCode = $process.ExitCode
    $report = '（自己検査の結果ファイルが作成されませんでした）'
    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        $raw = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8
        if ($null -ne $raw) { $report = $raw.TrimEnd() }
    }
    if ($exitCode -ne 0) {
        throw "埋め込みペイロードの自己検査に失敗しました（終了コード $exitCode）。`n$report"
    }
    Write-Host $report

    # 埋め込まれた manifest.json が、指定したペイロードの manifest.json と同じであることを確かめる（AA-01）。
    # manifest には各ファイルの SHA-256 が入っており、自己検査が埋め込みファイルと manifest の一致を確かめているので、これでペイロード全体が保証される。
    $manifestShaMatch = [regex]::Match($report, '(?m)^manifestSha256=([0-9a-f]{64})\r?$')
    if (-not $manifestShaMatch.Success) {
        throw "自己検査の結果に manifestSha256 の行がありません（exe の自己検査が古い形式か、結果が壊れています）。`n$report"
    }

    $embeddedManifestSha256 = $manifestShaMatch.Groups[1].Value
    if ($embeddedManifestSha256 -cne $expectedManifestSha256) {
        throw ("exe に埋め込まれたペイロードが、指定したペイロードと一致しません（前回のビルドの内容が埋め込まれた可能性があります）。`n" +
            "埋め込まれた manifest.json の SHA-256: $embeddedManifestSha256`n" +
            "指定した manifest.json の SHA-256:   $expectedManifestSha256（$manifestPath）")
    }
    Write-Host '埋め込まれた manifest.json が、指定したペイロードと一致することを確認しました。'

    # 5. 出力先へコピーし、結果を表示する（出力先の他のファイルは消さず、あれば警告する）
    $exePath = Join-Path $OutputDirectory $exeName
    Copy-Item -LiteralPath $stagedExe -Destination $exePath -Force

    $otherEntries = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object { $_.Name -ne $exeName })
    if ($otherEntries.Count -gt 0) {
        Write-Warning ('出力先に今回のビルド以外のファイルがあります（配布物には含まれません）: ' + (($otherEntries | ForEach-Object { $_.Name }) -join ', '))
    }

    $exe = Get-Item -LiteralPath $exePath
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("完了: {0}（{1:N1} MB、バージョン {2}）" -f $exe.FullName, ($exe.Length / 1MB), $patchVersion)
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
