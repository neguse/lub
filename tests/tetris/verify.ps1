param([string]$Binary = "build-release/lub.exe")
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "../..")
$previousScenario = $env:TETRIS_SCENARIO
try {
    dotnet run --project tests/tetris/TetrisTests.csproj
    if ($LASTEXITCODE -ne 0) { throw "Tetris rule tests failed" }
    New-Item -ItemType Directory -Force out/tetris-trial | Out-Null
    $env:TETRIS_SCENARIO = "clear"
    $output = & $Binary samples/29_tetris/Tetris29.csproj --capture out/tetris-trial/clear.png --capture-frame 35 --fixed-dt 0.016666667 2>&1
    $result = $LASTEXITCODE
    $output | Set-Content -Encoding utf8 out/tetris-trial/verify.log
    $output | Write-Output
    $text = $output -join "`n"
    if ($result -ne 0 -or $text -match "lua error|FATAL|error in") {
        throw "Tetris runtime failed; see out/tetris-trial/verify.log"
    }
    if ($text -notmatch "TETRIS_SCENARIO lines=4 score=832 locked=1" -or
        $text -notmatch "captured frame 35") {
        throw "Expected scenario result or capture acknowledgment was missing"
    }
    Write-Output "TETRIS_VERIFY passed (rules, Lua scenario, capture acknowledgment)"
} finally {
    $env:TETRIS_SCENARIO = $previousScenario
    Pop-Location
}
