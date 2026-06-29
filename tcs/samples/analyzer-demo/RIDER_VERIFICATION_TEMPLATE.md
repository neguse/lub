# Rider verification template

`samples/analyzer-demo/analyzer-demo.csproj` を Rider で開いた実機確認結果を記録するためのテンプレート。
確認後、この内容を `q.md` の Q12 に反映する。

## Environment

- Date:
- OS:
- Rider version:
- .NET SDK:
- Project opened as:
  - repository root
  - `samples/analyzer-demo/analyzer-demo.csproj`

## Pre-checks

- `bash run-tests.sh`: pass / fail / not run
- `samples/analyzer-demo/verify-inspectcode.sh`: pass / fail / not run
- `dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental`: pass / fail / not run

## Rider inspection result

- `TCS1001` x4 shown in editor:
  - `StructDeclaration`: yes / no
  - `LocalFunctionStatement`: yes / no
  - `TryStatement`: yes / no
  - `ThrowStatement`: yes / no
- `TCS1002` x1 shown in editor:
  - `System.IO.File.ReadAllText`: yes / no
- Build tool window shows `TCS1001` x4 / `TCS1002` x1: yes / no
- `.editorconfig` severity override is reflected in Rider:
  - `dotnet_diagnostic.TCS1002.severity = error`: yes / no

## Decision

- Decision: go / no-go
- Reason:
- Evidence:

## If go

Add product tasks to `doc/tasks.md` using this shape:

```markdown
### T123: tcs analyzer package を正式導線にする
- 目的: Rider / dotnet build / CI で同じ tcs 準拠診断を得られる package 導線を整える
- 作業:
  - analyzer package metadata / README / versioning policy を整える
  - PackageReference sample を維持する
  - `run-tests` の package consumer gate を正式 CI gate とする
  - release 手順を README に追加する
- 完了条件: local nupkg consumer と Rider 実機で TCS1001/TCS1002/TCS1003 の severity が一致する

### T124: tcs check / analyzer / transpiler diagnostics の一致を継続検証する
- 目的: shared compliance facts の変更時に IDE/build/check/transpile の判定ずれを防ぐ
- 作業:
  - analyzer-demo fixture を TCS1003 も含む形へ拡張するか判断する
  - CLI fixture と analyzer test の expected diagnostics を同じケースで比較する
  - support matrix の診断対象と test case を同期する
- 完了条件: 代表 unsupported syntax / API / collection null が analyzer, `tcs check`, transpiler warning で同じ ID になる
```

## If no-go

Record the no-go reason in `q.md` and add one or more alternative tasks:

```markdown
### T123: Rider analyzer no-go 代替導線を決める
- no-go 理由:
- 代替案:
  - Rider plugin
  - external tool
  - CLI watcher integration
- 完了条件: Rider 上で保存前または保存直後に tcs 準拠診断を確認できる導線を1つ選ぶ
```
