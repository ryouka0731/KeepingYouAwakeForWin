# コードメトリクスレポート

KeepingYouAwake for Windows のテスト網羅性とコード品質の指標です。

- 計測日: 2026-10-21
- 比較対象: テスト拡充の前は `main`（52b0bec、v1.0.0 時点）、後は PR #6
- 計測方法: 後半の「最新の計測値」は [`scripts/code_metrics.py`](../scripts/code_metrics.py) が CI で出力したものをそのまま貼っています。前後比較の表も同じスクリプトと同じ除外条件で計測しました。

## 要約

| 指標 | 改善前 | 改善後 |
|---|---:|---:|
| テストの総数 | 231（＋CI のスモークテスト 1 本） | **382** |
| C# Core のユニットテスト | 110 | **193** |
| Windows アプリのコンポーネント／プラットフォームテスト | 0 | **41** |
| Python のテスト（ユニット・プロトコル） | 121 | **137** |
| e2e テスト（配布用 exe を実際に操作） | 0（スモークテスト 1 ステップのみ） | **11** |
| Core の行／分岐カバレッジ | 93.6% / 80.0% | **99.1% / 93.8%** |
| Windows アプリの行／分岐カバレッジ | 0% / 0%（未計測） | **85.7% / 62.2%** |
| Python ツールの行／分岐カバレッジ | 96%（行と分岐の合算） | **98.7% / 98.2%** |
| 平均循環的複雑度（CCN） | 3.22 | **2.85** |
| 最大 CCN | 21 | **10** |
| CCN が 10 を超える関数 | 10 | **0** |
| 50 NLOC を超える関数 | 4 | **0** |
| 引数が 5 個を超える関数 | 1 | **0** |
| 本番の C# コード行数 | 4,702 | 5,087 |
| テストコード行数 | 2,430 | 4,419 |

カバレッジと複雑度の閾値は CI（`metrics` ジョブ）で毎回チェックします。どれかを下回ると PR が失敗します。

| 閾値 | 値 |
|---|---|
| Core のカバレッジ | 行 97% 以上、分岐 90% 以上 |
| Windows アプリのカバレッジ | 行 80% 以上、分岐 55% 以上 |
| Python のカバレッジ | 行 97% 以上、分岐 95% 以上 |
| 関数ごとの上限 | CCN 10 以下、50 NLOC 以下、引数 5 個以下 |

## テストの層

| 層 | 場所 | 実行環境 | 検証している内容 |
|---|---|---|---|
| ユニット | `tests/KeepingYouAwake.Core.Tests` | すべての OS | セッションの開始と終了、各トリガー、「トリガーは自分で開始したセッションしか止めない」というルール、設定とログの読み書き、書き込み失敗からの回復、URL コマンド、Win32 の戻り値の解釈、トレイ表示の組み立て |
| API | `UpdateCheckerApiTests`、`tools/mcp-server/tests/test_mcp_protocol.py` | すべての OS | GitHub Releases API に送るリクエストとレスポンスの扱い（HTTP はスタブ）。MCP サーバーを実際に起動して stdio の JSON-RPC で通信し、ツール一覧・呼び出し・スキーマ検証を確認 |
| コンポーネント | `tests/KeepingYouAwake.Tests`（`ComponentTests`、`ServiceTests`） | Windows | トレイメニュー、設定画面の 7 タブ、各ダイアログ、アクティビティログ画面。WinForms のコントロールを実際に操作し、設定が書き換わることと画面に反映されることを確認 |
| プラットフォーム | `tests/KeepingYouAwake.Tests`（`PlatformTests`） | Windows | 実際の Win32 API（電源要求、電源状態、ディスプレイ構成、Native Wifi、Core Audio、CPU 時間、入力）。レジストリは専用のサンドボックスに書く。単一インスタンス用の名前付きパイプ、UI スケジューラ |
| e2e | `tools/e2e/test_e2e_windows.py` | Windows CI | 配布用の exe を起動し、`kya` CLI（URL スキーム経由）、転送されたコマンドライン引数、`settings.json` の書き換えで操作。アクティビティログと `powercfg /requests` で結果を確認。時間切れ、監視アプリ、ダウンロードトリガー、ユーザーのセッションを保護する動作、正常終了、強制終了からの復旧を含む |

## 実施した改善

### テストしやすくするための構造変更

- **Win32 の戻り値の解釈を Core に移動**: Win32 から受け取った値の意味付けを `WindowsDecoding` にまとめました。
  - 対象: `SYSTEM_POWER_STATUS`、ディスプレイの接続方式、音声出力デバイスの種類、Native Wifi のバッファ構造。
  - 効果: P/Invoke 層は値の取得だけになり、解釈の部分はどの OS でもテストできます。Wi-Fi のバッファは、合成したデータでテストしています。
- **トレイの表示内容を Core に移動**: メニュー構成・ツールチップ・通知文を `TrayPresenter` が作ります。WinForms 側は描画とクリック処理だけになりました。
- **差し替え可能な依存を整理**:
  - `TrayOptions`: OS サービス、レジストリ、アップデート確認に使う HTTP を差し替えられます。
  - `ShellIntegration`: レジストリのルートとキーの接頭辞を指定でき、テストではサンドボックスに書き込みます。
  - `UpdateService`: HTTP ハンドラを注入できます。
  - `KEEPINGYOUAWAKE_DATA_DIR`: e2e テストでデータの保存先を分けるための環境変数です。
- **終了コマンドを追加**: `--quit` / `keepingyouawake:///quit`。e2e テストや自動化から正常終了させるために追加しました。

### 複雑度を下げた関数

| 関数 | CCN（前 → 後） | 内容 |
|---|---:|---|
| `UpdateChecker.SelectUpdate` | 21 → 9 | リリース 1 件の解釈（`ParseRelease`）と新旧比較（`IsNewer`）を分離 |
| `call_tool`（MCP） | 15 → 2 | ツール名から処理を引く辞書に置き換え |
| `CoreAudioProbe.IsDefaultOutputExternal` | 14 → 9 | デバイスの分類を Core に移し、COM の解放を共通化 |
| `AppController.ReconcileSessionSideEffects` | 14 → 3 | マウスジグラー・ディスクのスリープ防止・ディスプレイのスリープ許可に分割 |
| `SettingsForm.BuildDurations` | 13 → 7 | 一覧の再描画・追加・リセットを別メソッドに |
| `SettingsForm.BuildWatchedItems` | 12（90 NLOC） → 1 | 監視項目の種類ごとに組み立てを分割。リストと設定の対応は `ListBinding` レコードで表現 |
| `WlanWiFiProbe.Read` | 12 → 3 | ハンドル管理、インターフェース列挙、SSID 取得に分割し、バッファの解釈は Core へ |
| `PowerRequestAssertion.Acquire` | 12 → 4 | 電源要求の作成とディスプレイ要求の切り替えを分離 |
| `CpuLoadMonitor.SampleOnce` | 12 → 4 | 使用率の計算（純粋関数）、継続時間の加算、イベント発火を分離 |
| `AppController.Activate` | 11 → 8 | 時間の正規化と検証を `TryNormalizeInterval` に分離 |
| `list_tools`（MCP） | 2 → 1（70 NLOC → 2 NLOC） | ツール定義をモジュールのデータに移動 |
| `TrayApplicationContext` のコンストラクタ | 引数 6 → 3 | 差し替え用の引数を `TrayOptions` にまとめた |

### テストで見つかった不具合・仕様

- **Windows 固有の挙動**: 隠しファイルは属性を外さないと上書きできません。アプリ側は対応済みで、テストの書き方を修正しました。WinForms は、選択されていないタブにあるボタンへの `PerformClick` を無視します。テストは利用者と同じくタブを選んでから押すようにしました。
- **MCP サーバーの実際の挙動**: 不正な引数は、ツールの処理より先に SDK のスキーマ検証で弾かれます。プロトコルレベルのテストで初めて確認できました。
- **アプリ全体で共有される UI 状態**: `UiThread` と `SystemEvents` はプロセス全体で 1 つです。WinForms のテストは並列ではなく 1 件ずつ実行するようにしました。

## 未カバーの部分とその理由

- **`Windows/NetworkAndAudio.cs`（26.9%）**: 未カバーなのは、Wi-Fi アダプタと音声デバイスがあるときだけ通る P/Invoke と COM の呼び出し部分です。CI のランナーにはどちらもないため実行できません。バッファや分類の解釈は Core のテストで 100% カバーしています。
- **`SettingsForm` のモーダルダイアログを開く処理**: ファイル選択、フォルダー選択、確認メッセージの各ダイアログは、操作されるまで処理が戻りません。ダイアログ自体は単体でテストしています。
- **`Program.cs`**: アプリの起点で、e2e テストが実際の exe として毎回実行しています。ユニットテストのカバレッジ計測からは除外しています（`[ExcludeFromCodeCoverage]`）。
- **`PowerRequestAssertion` の代替経路**（`SetThreadExecutionState`）: `PowerCreateRequest` が失敗したときだけ使われます。

## 再計測の方法

```powershell
dotnet test tests/KeepingYouAwake.Core.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/core
dotnet test tests/KeepingYouAwake.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/app   # Windows のみ
dotnet tool restore
dotnet tool run reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:Cobertura "-filefilters:-**/obj/**"
pytest --cov=tools/cli --cov=tools/mcp-server --cov-branch --cov-report=xml:python-coverage.xml
python scripts/code_metrics.py --dotnet coverage/Cobertura.xml --python python-coverage.xml --update docs/code-metrics.md --check
```

CI では毎回、同じ表を GitHub Actions のジョブサマリーに出力しています。

## 最新の計測値

<!-- metrics:begin -->
### Coverage

| Component | Line | Branch | Covered / coverable lines |
|---|---:|---:|---:|
| `KeepingYouAwake` (.NET) | 85.7% | 62.2% | 1036 / 1209 |
| `KeepingYouAwake.Core` (.NET) | 99.1% | 93.8% | 1288 / 1300 |
| Python tools (`kya`, MCP server) | 98.7% | 98.2% | 887 / 899 |

Lowest-covered files:

| File | Line coverage |
|---|---:|
| `KeepingYouAwake/Windows/NetworkAndAudio.cs` | 26.9% (18/67) |
| `KeepingYouAwake/UI/UpdateService.cs` | 78.6% (33/42) |
| `KeepingYouAwake/UI/SettingsForm.cs` | 83.2% (333/400) |
| `KeepingYouAwake/Windows/ShellIntegration.cs` | 85.4% (70/82) |
| `KeepingYouAwake/Windows/PowerServices.cs` | 86.0% (117/136) |
| `KeepingYouAwake.Core/DriveAliveTimer.cs` | 92.0% (81/88) |
| `KeepingYouAwake/Windows/WinFormsScheduler.cs` | 93.3% (28/30) |
| `kya_mcp_server.py` | 93.9% (168/179) |

### Complexity

| Metric | Value |
|---|---:|
| Functions | 317 |
| Function NLOC (total) | 3142 |
| Average cyclomatic complexity (CCN) | 2.85 |
| Max CCN | 10 |
| Functions with CCN > 10 | 0 |
| Functions longer than 50 NLOC | 0 |
| Functions with more than 5 parameters | 0 |

Most complex functions:

| Function | File | CCN | NLOC |
|---|---|---:|---:|
| `DriveAliveTimer::WritePing` | `src/KeepingYouAwake.Core/DriveAliveTimer.cs:126` | 10 | 30 |
| `PlatformServices::OnPowerChanged` | `src/KeepingYouAwake.Core/AppController.cs:513` | 10 | 22 |
| `ActivityLogEntry::Parse` | `src/KeepingYouAwake.Core/ActivityLogger.cs:210` | 10 | 10 |
| `ActivityLogEntry::RecordStarted` | `src/KeepingYouAwake.Core/ActivityLogger.cs:73` | 9 | 28 |
| `CoreAudioProbe::IsDefaultOutputExternal` | `src/KeepingYouAwake/Windows/NetworkAndAudio.cs:96` | 9 | 28 |
| `ReleaseInfo::ParseVersion` | `src/KeepingYouAwake.Core/UpdateChecker.cs:68` | 9 | 17 |
| `ScheduleWindow::Contains` | `src/KeepingYouAwake.Core/ScheduleWindow.cs:27` | 9 | 15 |
| `ReleaseInfo::SelectUpdate` | `src/KeepingYouAwake.Core/UpdateChecker.cs:30` | 9 | 14 |
| `TrayOptions::Perform` | `src/KeepingYouAwake/UI/TrayApplicationContext.cs:242` | 9 | 14 |
| `ReleaseInfo::ParseRelease` | `src/KeepingYouAwake.Core/UpdateChecker.cs:49` | 9 | 11 |
<!-- metrics:end -->
