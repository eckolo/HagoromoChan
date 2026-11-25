# M08 実行環境・起動終了管理 v1.0

- 対象: M08 / milestone/M08-startup
- 基準: main 411cf03479b45a5a4c60379ee3b40e940d78fb7f。M03 v2.3完了記録を継承。
- 状態: 2026-11-25 ユーザーの方式承認・実機確認・最終受入を受領。v1.0.4を完了マージ対象とする。個別未検査は実機確認手順に維持。
- 通常利用は単一のアプリケーションの起動・終了・再起動として扱う。右クリックの「スタートアップ登録」は設けない。「接続状態」は既存状態窓の展開トグルとする。

## 1. 責務

HagoromoChan.exe（Launcher / WinExe、通常ウィンドウなし）がDesktop、Relay、公式tunnel-clientを所有する。stdioアダプターはtunnel-client自身が起動・監視する。既存のRelayとstdio、SQLite、HTTP、MCPイベント・返信の責務は維持し、外部通信方式を変更しない。

- ローカルデータディレクトリのlauncher.lockで管理アプリを一意にする。既存desktop.lock、relay.lockとポート48732を調べ、旧版・他のプロセスを勝手に取り込まない。
- 同一Windowsユーザー限定の名前付きパイプで、停止・全体再起動・Desktop単体再起動・Tunnel単体再起動の固定コマンドを受け付ける。データディレクトリからパイプ名を導出。コマンドは1バイト、受付応答も1バイト、時間制限付き。任意コマンドや本文・キーを渡さない。
- 子プロセスを個別の匿名Job Objectに属させる。CreateProcessWのSTARTUPINFOEX、PROC_THREAD_ATTRIBUTE_JOB_LISTで生成時に所属を確定。PROC_THREAD_ATTRIBUTE_HANDLE_LISTで継承ハンドルを標準入出力用に限定。KILL_ON_JOB_CLOSEを設定して異常終了時の残留を抑止する。Windows 10以降のAPIを使用する。所有外のプロセスに対するTerminateProcessや名前による一括停止は行わない。
- Windowsログオフ・シャットダウンはSessionEndingから停止を要求する。OS側の終了期限により正常停止を完了できない場合もあり、Job Objectは最後の手段である。

## 2. 起動・停止

通常起動: 配置・多重起動・旧プロセス確認 → Relay起動 → /healthの管理セッション一致・応答確認（上限20秒） → 設定済みならTunnel起動 → Desktop起動。トンネル準備を待たずDesktopを利用可能にする。Relay自体の起動失敗・ポート競合は処理を止めて通知する。未設定またはトンネル異常時はDesktop/Relayを動かし、状態枠で示す。

通常終了: Desktopへの停止イベント（新規送信を停止、既存の受理確認用ファイル・位置設定を保存）→ Tunnelの専用コンソールへCtrl+C（全参加PIDのJob所属を照合）→ そのJob全体の空確認（stdioを含む）→ Relayへの停止イベント（ASP.NET Core StopApplication、SQLite解放）→ 全Job終了確認 → Launcher終了。通常のGUI「終了」は全体終了。「再起動」は同じLauncher内で全体停止後に起動し直す。Alt+F4等でDesktopが終了した場合も全体停止する。個別開発起動の場合に限りGUI終了はDesktopのみ。

正常停止をDesktop 8秒、Tunnel 12秒、Relay 20秒まで待ち、超過時のみ所有Jobを終了して追加5秒確認する。上限は候補値であり実測ではない。自動再起動ループは設けず、Relayが異常終了したらTunnelを停止してDesktopに異常表示する。トンネルが異常終了した場合も状態表示・再起動を提供する。受付済み／結果不明の送信は既存の冪等性・pending-message.jsonを維持する。未送信の下書きの永続化は従来対象外であり、再起動前には注意する。購読やDBを削除・再登録しない。

## 3. 設定・接続状態

現行の公式tunnel-client 0.0.21（ユーザー実機: 714f2d67844b660af25754f1d19b0360b346b13b）を前提とする。既存hagoromoプロファイルのファイルを読み、配置済みRelay.exe --mcp-stdioをコマンドに上書きする。2026-11-25、ユーザーは --mcp.stdio-send-initialized-notification=true で購読成功を報告。管理起動に固定し、既存プロファイルは編集しない。Configure.ps1は明示 -ProfileFile がない場合に公式クライアント自身の profiles list --json を利用して既存hagoromoのパスを解決する（PowerShellの $HOME を用いた推定は行わない）。見つからなければ設定保存前に停止し、新規プロファイルを作成しない。Configure.ps1が実行ファイルの絶対パス、SHA256、バージョン、プロファイルの絶対パス、ログレベル、WindowsユーザーのDPAPIで暗号化したruntimeキーをlauncher.jsonに保存する。実行ファイルが変更された場合は明示的な再設定を要求する。別バージョンは明示オプションと互換性確認が必要。キーはトンネル起動の環境変数だけに渡し、Relay/stdio/Desktopに不要なOpenAIキーを渡さない、または起動時に消去する。キーはGit・引数・診断ログに書かない。平文HTTPログは無効化する。OSユーザー自身のアクセス権は信頼境界であり、同じユーザーによる読み取りを防ぐものではない。

管理状態はruntime-status.jsonに一時ファイルから置換して記録。セッションIDと観測時刻が一致・新鮮な状態のみ表示する。Relayの管理セッションに一致する/health、Tunnelのループバックreadyz、各PID、起動・停止・異常を区別する。トンネルのreadyzはクラウド側購読成立・Dot実行の証明ではない。既存の「購読有効（ローカル中継）」と「新着」規則、M06の詳細な診断・復旧は変更しない。右クリックの「接続状態」と既存状態窓の⌄/⌃で同じ枠を展開・縮小する。健康状態でも明示的に表示可能。右クリックのスタートアップ登録項目は追加しない。状態枠は展開・縮小とも羽衣ちゃん本体の実表示幅と一致させ、上下配置では左右端を揃える。キャラクターのサイズ・位置による従来の空き幅の閾値は廃止する。本文の改行・折り返しに応じて高さを決め、開いている会話領域を覆わない近接配置を優先する。両方が収まらない場合は状態枠を一時的に隠し、既存の右クリックから詳細コピー等を利用できるようにする。詳細枠の内容が小サイズで十分読めるかは実機検査事項とする。

## 4. 配置・更新

通常の配置先（別データディレクトリを指定しない場合）:
- %LOCALAPPDATA%/HagoromoChan/app/HagoromoChan.exe
- app/Desktop/ と app/Relay/（別のpublish成果物）
- 同じデータディレクトリ直下の既存DB、pending-message.json、desktop-ui.json、relay.token、launcher.json、locks、runtime-status.json、logs/
- トンネル本体とプロファイルは既存ユーザー管理の絶対パスを参照。自動ダウンロード・アップグレードしない。

Build.ps1は新規app-stage-*にpublishし、通常起動はビルドしない。Update.ps1はビルド成功後に管理アプリの停止を要求し、launcher.lockとdeployment.lockで多重更新・起動と競合しないようにし、旧appをapp-previous-*へ退避、配置候補をappへ移動する。移動失敗時は旧appに戻す。旧配置は保持して手動復旧可能にする。DB・購読・鍵はappの外にあるため変更しない。Desktop-onlyは共有入力指紋が一致する場合だけLauncher/Relayの既存成果物をコピーしてDesktopをpublishする。配置時は安全のため全体を止めて切り替える（Relay/Tunnel稼働維持でのDesktop単体更新は本候補未実装）。Desktop単体再起動はビルドなしで実行可能。Git更新はユーザーが明示的に行い、Update.ps1はGitを変更しない。

Startup.ps1は標準ユーザーのスタートアップフォルダーにHagoromoChan.lnkを明示的に登録／解除する。タスクスケジューラ、サービス、管理者権限、トンネル個別登録は使わない。通常起動と同じ固定exeを指す。検証用カスタムデータディレクトリはスタートアップ登録の対象外。Windows側のStartup Appsの無効設定は尊重する。

## 5. 制約・後続

現環境には.NET SDK、Windows PowerShell、Windows GUIがない。Windows APIの実際の挙動、Job Objectの異常終了、コンソール停止、DPAPI復号、ショートカット登録、MCP往復、ビルド・回帰検査は未実行。別のtunnel-client実行中は保守的に起動・更新を拒否するため無関係な公式トンネルとの同時運用も制約される。遠隔ホストに存在する同一トンネルはローカルから検知できない。Windows 11で旧版プロセスを正常停止してから移行し、[実機確認手順](../operations/M08-verification.md)に従う。M06とM14の実装は着手しない。

参照: [公式トンネル設定 v0.0.21](https://github.com/openai/tunnel-client/blob/714f2d67844b660af25754f1d19b0360b346b13b/docs/configuration.md)、[UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[Create processes](https://learn.microsoft.com/en-us/windows/win32/procthread/creating-processes)。

### v1.0.3 実機指摘への修正

v1.0.2ではユーザーがLauncher、Relay、Desktopの管理起動と状態枠展開を確認。Tunnelは起動直後にexit code 1となり、stdio実行ファイルパスの区切り文字がすべて脱落するエラーを記録した。公式v0.0.21固定SHAの `parseCommandArgv` は二重引用符内でもバックスラッシュをエスケープとして処理する。Windowsプロセス生成用の引用とは別の `BuildTunnelStdioCommand` で各バックスラッシュを二重化し、外側は既存のWindows引数引用を維持する。認証設定・既存プロファイル・DB・購読は変更しない。公式ソース: [config.go](https://github.com/openai/tunnel-client/blob/714f2d67844b660af25754f1d19b0360b346b13b/pkg/runtimeconfig/config.go#L2835-L2901)。

状態枠の幅が中・大で逆転し、吹き出しと重なる旨の実機指摘を受領。固定300 DIP案は採用せず、ユーザーの追加指示に従い本体実表示幅に一致させる。M08の既存状態枠調整の範囲内で扱い、M03を再開したり別IDを追加したりしない。会話領域が開いている場合は重なりを許容する従来のフォールバックを行わない。小さな作業領域で表示できない場合の右クリック代替操作を維持する。幅・位置・詳細の可読性はWindows実機未検証。

### v1.0.4 アプリケーションアイコン

ユーザー指定の既存プラグイン用画像 `hagoromo_plugin_icon.png` をもとに16/32/48/256ピクセルを含む共通ICOを追加。256ピクセル画像は元PNGをそのまま格納し、小サイズは縮小。LauncherとDesktopの `ApplicationIcon` で同じICOを指定し、通常のpublish時にexeへ埋め込む。配置後の手動操作やアイコン変更スクリプトは不要。Relay、Mascot.png、既存プラグイン設定、通信とDBは変更しない。[素材情報](../../src/HagoromoChan.Launcher/Assets/README.md)。v1.0.3の管理起動・会話往復成功はユーザー報告で確認済みだが、v1.0.4の実機ビルド・exeアイコン表示は未確認。

### 完了時の確認範囲

v1.0.3（`ce49dcca9754df16ba5eb86b335474754e28ea26`）で管理起動と会話往復成功、v1.0.4（`bb6dc09fa8bef12fba58b257314ad20f556a745f`）でユーザーの全般的な実機動作成功報告と完了指示を受領した。前節までの未検証記述は各版作成時点の記録であり、後の受入報告を優先する。ただし個別の回帰・異常終了・ログオン等の未取得結果を合格としない。M08完了は2親マージ・読戻し成立時のみ有効。M06の通信状態の診断・障害復旧、M14の新着解除の実装は含めない。
