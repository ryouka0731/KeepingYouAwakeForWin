# コードメトリクスレポート

KeepingYouAwake for Windows のテスト網羅性とコード品質の指標です。

- 計測日: 2026-10-21
- 比較対象: テスト拡充の前は `main`（52b0bec、v1.0.0 時点）、後は PR #6・#8・#9
- 計測方法: 前後どちらの値も [`scripts/code_metrics.py`](../scripts/code_metrics.py) で集計しています。
  - 「改善前」の Core と Python は、52b0bec を別の作業ツリーに展開し、同じツール（coverlet、ReportGenerator、pytest-cov、lizard）・同じ除外条件で計測し直した値です。
  - 改善前の Windows アプリにはテストがありませんでした。
  - 「改善後」と後半の「最新の計測値」は、Windows CI で計測した値を貼り付けています。

## 要約

| 指標 | 改善前 | 改善後 |
|---|---:|---:|
| テストの総数 | 231（＋CI のスモークテスト 1 本） | **454** |
| C# Core のユニット・性質ベーステスト | 110 | **241** |
| Windows アプリのコンポーネント／プラットフォームテスト | 0 | **65** |
| Python のテスト（ユニット・プロトコル） | 121 | **137** |
| e2e テスト（配布用 exe を実際に操作） | 0（スモークテスト 1 ステップのみ） | **11** |
| Core の行／分岐カバレッジ | 93.6% / 80.0% | **99.2% / 96.2%** |
| Windows アプリの行／分岐カバレッジ | 0% / 0%（テストなし） | **96.1% / 81.2%** |
| Python ツールの行／分岐カバレッジ | 96.3% / 90.3% | **98.7% / 98.2%** |
| 平均循環的複雑度（CCN） | 3.22 | **2.77** |
| 最大 CCN | 21 | **10** |
| CCN が 10 を超える関数 | 10 | **0** |
| 50 NLOC を超える関数 | 4 | **0** |
| 引数が 5 個を超える関数 | 1 | **0** |
| 変異スコア（Core、Stryker.NET） | 81.0%（最初の正しい計測時点） | **90.9%** |
| 性質ベース／モデルベーステスト | 0 | **16**（性質 15、モデル 1） |
| 本番の C# コード行数 | 4,702 | 5,087 |
| テストコード行数 | 2,430 | 4,419 |

カバレッジと複雑度の閾値は CI（`metrics` ジョブ）で毎回チェックします。どれかを下回ると PR が失敗します。

| 閾値 | 値 |
|---|---|
| Core のカバレッジ | 行 97% 以上、分岐 90% 以上 |
| Windows アプリのカバレッジ | 行 94% 以上、分岐 78% 以上 |
| Python のカバレッジ | 行 97% 以上、分岐 95% 以上 |
| 関数ごとの上限 | CCN 10 以下、50 NLOC 以下、引数 5 個以下 |

## テストの層

| 層 | 場所 | 実行環境 | 検証している内容 |
|---|---|---|---|
| ユニット | `tests/KeepingYouAwake.Core.Tests` | すべての OS | セッションの開始と終了、各トリガー、「トリガーは自分で開始したセッションしか止めない」というルール、設定とログの読み書き、書き込み失敗からの回復、URL コマンド、Win32 の戻り値の解釈、トレイ表示の組み立て |
| API | `UpdateCheckerApiTests`、`tools/mcp-server/tests/test_mcp_protocol.py` | すべての OS | GitHub Releases API に送るリクエストとレスポンスの扱い（HTTP はスタブ）。MCP サーバーを実際に起動して stdio の JSON-RPC で通信し、ツール一覧・呼び出し・スキーマ検証を確認 |
| コンポーネント | `tests/KeepingYouAwake.Tests`（`ComponentTests`、`ServiceTests`、`DialogAndProbeTests`） | Windows | トレイメニュー、設定画面の 7 タブ、各ダイアログ、アクティビティログ画面。WinForms のコントロールを実際に操作し、設定が書き換わることと画面に反映されることを確認。ダイアログ経由の操作（時間の追加・リセット、監視項目の追加、アクティブ時間の編集、アップデート確認の結果表示）は、回答を用意した `IUserPrompts` で検証。本物のダイアログ（WinForms 製・メッセージボックス・ファイル／フォルダー選択）は、表示中の待機ループの中から自動で応答させて検証 |
| プラットフォーム | `tests/KeepingYouAwake.Tests`（`PlatformTests`） | Windows | 実際の Win32 API（電源要求、電源状態、ディスプレイ構成、Native Wifi、Core Audio、CPU 時間、入力）。レジストリは専用のサンドボックスに書く。単一インスタンス用の名前付きパイプ、UI スケジューラ |
| e2e | `tools/e2e/test_e2e_windows.py` | Windows CI | 配布用の exe を起動し、`kya` CLI（URL スキーム経由）、転送されたコマンドライン引数、`settings.json` の書き換えで操作。アクティビティログと `powercfg /requests` で結果を確認。時間切れ、監視アプリ、ダウンロードトリガー、ユーザーのセッションを保護する動作、正常終了、強制終了からの復旧を含む |

### 性質ベース・モデルベース・変異テスト

| 種類 | 場所 | 内容 |
|---|---|---|
| 性質ベース | `PropertyTests`（FsCheck） | ランダムに生成した入力で、どんな入力でも成り立つべき性質を検証します。失敗したときは、失敗する最小の入力まで自動で絞り込みます（シュリンク）。対象は次のとおりです。<br>・アクティブ時間の判定を、別の方法で実装した判定結果（分単位で数え上げる方法）と突き合わせる<br>・バージョンの比較が全順序になっていること<br>・URL で指定した時間がそのまま往復すること<br>・残り時間の表示を読み戻すと元の秒数になること<br>・監視リストの編集が、大文字小文字を区別しない集合として振る舞うこと<br>・インスタンス間メッセージが往復すること<br>・CPU 使用率が 0〜100% に収まること<br>・電源状態の解釈がどの値の組でも結果を返すこと |
| モデルベース | `ControllerModelPropertyTests` | ユーザー操作、アプリの起動と終了、ダウンロード、AC の抜き差し、ディスプレイ接続、時間経過をランダムな順番で与え、各ステップの後に次の 3 点を確認します。<br>・電源要求を持っているのはセッション中だけ<br>・手動で始めたセッションをトリガーが止めない<br>・アクティビティログで開いたままの記録が 1 件だけ<br>トリガーの所有者チェックをわざと壊すと、16 通り目のランダムな手順で検出しました。 |
| 変異テスト | `stryker-config.json`、`.github/workflows/mutation.yml` | Stryker.NET が Core にわざと小さな不具合（変異）を入れ、テストが落ちるかを調べます。CI では毎週と手動で実行します。<br>最初は既定の「テストごとの網羅判定」で計測して 57.3% でした。ところが生き残った変異を手で再現すると、実際にはテストが落ちるものが含まれていました。そこで、全テストを全変異に対して実行する設定にしました。<br>正しい初期値 81.0% から、生き残った変異をもとにテストを追加して 90.9% まで上げました。 |

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
- **ダイアログ表示を差し替え可能に**: 設定画面のダイアログ表示をすべて `IUserPrompts` 経由にしました。
- **Wi-Fi・音声デバイスの API 呼び出しを分離**: Native Wifi と Core Audio の呼び出しを、薄いインターフェース（`IWlanNative` / `IAudioEndpointReader`）の向こう側に置きました。アダプタなし、アクセス拒否、SSID の伏せ字化、複数アダプタ、COM エラーといった結果を、ハードウェアなしでテストできます。

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
- **カバレッジ計測の欠落**: coverlet が `System.Windows.Forms` を解決できず、アプリ本体（`KeepingYouAwake.dll`）を計測対象から黙って外していました。テスト基盤の診断ログで原因を特定し、`PreserveCompilationContext` で解消しました。あわせて、レポートにコンポーネントが欠けていたら閾値チェックを失敗させるようにしました。
- **「ログイン時に起動」のエラーが 2 回表示される**: 設定に失敗したとき、チェックボックスを実際の状態に戻す処理が変更イベントをもう一度発生させ、同じエラーダイアログが 2 回出ていました。コンポーネントテストで見つけて修正しました。
- **変異テストのツール側の誤判定**: Stryker の既定の網羅判定（テストごとに、どの変異を通るかを記録する方式）が、オブジェクトの生成時に実行されるコードの変異を「どのテストも通らない＝生き残り」と誤って数えていました。上で書いたとおり、全テストを実行する設定に切り替えています。
- **OK ボタンだけのメッセージボックス**: OK ボタンの ID を送っても閉じません（`WM_CLOSE` で閉じる）。自動応答のヘルパーに反映しました。

## 未カバーの部分とその理由

- **`Windows/NetworkAndAudio.cs`（60.3%）**: 残っているのは、Native Wifi と Core Audio を実際に呼び出す部分（`WlanNative`、`CoreAudioEndpointReader`）です。CI のランナーには Wi-Fi アダプタも音声デバイスもないため、成功時の処理を通せません。結果の判定と解釈は、偽の実装を使ったテストですべて検証しています。
- **`UpdateService.cs`（78.6%）**: 起動 30 秒後と 1 時間ごとに確認するタイマーが未カバーです。確認処理そのもの（`CheckIfDueAsync` / `CheckNowAsync`）はテスト済みです。
- **`PowerServices.cs`（86.0%）**: 未カバーなのは次の 3 種類です。いずれも、そのとき通る判断の部分は Core 側で偽の実装を使ってテスト済みです。
  - 電源要求 API が失敗したときだけ使う代替経路（`SetThreadExecutionState`）
  - 電源状態やディスプレイ構成が CI の実行中に実際に変わったときだけ通る更新処理
  - ディスプレイ構成を取得できなかったときのフォールバック
- **`ShellIntegration.cs`（85.4%）**: 未カバーなのは次の 3 種類です。
  - `OpenUrl`: 実際にブラウザーを起動するため、テストでは呼ばず、記録だけする偽の実装に差し替えています。
  - レジストリへの書き込みが拒否されたときと、名前付きパイプの I/O 例外の処理。
  - 実際の HKCU を使う既定のインスタンス。テストはレジストリのサンドボックスを使います。
- **`Program.cs`**: アプリの起点で、e2e テストが実際の exe として毎回実行しています。ユニットテストのカバレッジ計測からは除外しています（`[ExcludeFromCodeCoverage]`）。

## 生き残っている変異（約 80 件）の内訳

| 分類 | 例 | 理由 |
|---|---|---|
| 等価な変異 | 二重呼び出しを防ぐ判定の削除、`Dispose` の順序、`ConfigureAwait(false→true)`、上限ちょうどでの切り詰め（`<` と `<=`） | 振る舞いが変わらないため、どんなテストでも区別できません |
| Windows 専用の処理 | ping ファイルの隠し属性の付与・解除 | Stryker は macOS / Linux で動かしているため、このコードは実行されません（Windows 側の処理は Windows CI のテストで確認しています） |
| 意図的に起こすのが難しい I/O 障害 | 書き込み途中の失敗、ファイルを移動して置き換える処理の失敗 | 失敗を確実に再現できず、テストが不安定になります |

## 再計測の方法

```powershell
dotnet test tests/KeepingYouAwake.Core.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/core
dotnet test tests/KeepingYouAwake.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/app   # Windows のみ
dotnet tool restore
dotnet tool run reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:Cobertura "-filefilters:-**/obj/**"
pytest --cov=tools/cli --cov=tools/mcp-server --cov-branch --cov-report=xml:python-coverage.xml
python scripts/code_metrics.py --dotnet coverage/Cobertura.xml --python python-coverage.xml --update docs/code-metrics.md --check
dotnet tool run dotnet-stryker -f stryker-config.json   # 変異テスト（約 60 分）
```

CI では毎回、同じ表を GitHub Actions のジョブサマリーに出力しています。

## 最新の計測値

<!-- metrics:begin -->
### Coverage

| Component | Line | Branch | Covered / coverable lines |
|---|---:|---:|---:|
| `KeepingYouAwake` (.NET) | 96.1% | 81.2% | 1207 / 1256 |
| `KeepingYouAwake.Core` (.NET) | 99.2% | 96.2% | 1290 / 1300 |
| Python tools (`kya`, MCP server) | 98.7% | 98.2% | 887 / 899 |

Lowest-covered files:

| File | Line coverage |
|---|---:|
| `KeepingYouAwake/Windows/NetworkAndAudio.cs` | 60.3% (47/78) |
| `KeepingYouAwake.Core/DriveAliveTimer.cs` | 92.0% (81/88) |
| `KeepingYouAwake/Windows/WinFormsScheduler.cs` | 93.3% (28/30) |
| `kya_mcp_server.py` | 93.9% (168/179) |
| `KeepingYouAwake/Windows/ShellIntegration.cs` | 93.9% (77/82) |
| `KeepingYouAwake/UI/UserPrompts.cs` | 95.5% (21/22) |
| `KeepingYouAwake/UI/Dialogs.cs` | 96.9% (156/161) |
| `KeepingYouAwake/Windows/PowerServices.cs` | 97.9% (139/142) |

### Complexity

| Metric | Value |
|---|---:|
| Functions | 338 |
| Function NLOC (total) | 3197 |
| Average cyclomatic complexity (CCN) | 2.77 |
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
| `ReleaseInfo::ParseVersion` | `src/KeepingYouAwake.Core/UpdateChecker.cs:68` | 9 | 17 |
| `ScheduleWindow::Contains` | `src/KeepingYouAwake.Core/ScheduleWindow.cs:27` | 9 | 15 |
| `TrayOptions::Perform` | `src/KeepingYouAwake/UI/TrayApplicationContext.cs:252` | 9 | 14 |
| `ReleaseInfo::ParseRelease` | `src/KeepingYouAwake.Core/UpdateChecker.cs:49` | 9 | 11 |
| `PlatformServices::OnSessionEnded` | `src/KeepingYouAwake.Core/AppController.cs:271` | 9 | 10 |
| `_current_status` | `tools/mcp-server/kya_mcp_server.py:218` | 8 | 25 |
<!-- metrics:end -->
