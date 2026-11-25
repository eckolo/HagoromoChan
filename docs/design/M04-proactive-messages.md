# M04 Dotからの能動的な発言 v1.0 実装候補

基準 main: `78671596e4d90bffe3446af930182fa85ac4e966`
作業ブランチ: `milestone/M04-dot-proactive-messages`
状態: 2026-11-25 着手。受信経路の設計と実装候補を作成。クラウド側の利用可能性、実機受入・M09の調整は未確認。

## 1. 目的・境界

既存Dot（羽衣ちゃん）がWindowsアプリからの質問に紐付かない発言を、通常の会話履歴・吹き出しで届けられるようにする。既存 `reply` は一つのユーザー依頼に対する一つの最終回答専用のままとする。自発発言は別の追記操作。既存の購読、署名、イベント配信、返信待ち、未回答のキャンセルや障害復旧、新着解除規則、アクション素材・演出を変更しない。

### 実現可能性の区別

公式 [Dotスタートガイド](https://help.openai.com/ja-jp/articles/20001530-getting-started-with-your-dot) と [Dots privacy, security, and safety FAQs](https://help.openai.com/en/articles/20001529-dots-privacy-security-and-safety-faqs)、[MCP Events](https://developers.openai.com/plugins/build/mcp-events)、[Add custom MCP server](https://developers.openai.com/api/docs/guides/custom-mcp-server)（2026-11-25参照）により、Dotの定期タスク、接続済みプラグインの読み書き、MCPイベントの利用が公表されている。一方、Dotの自発的な調査機能はプラグインへの書き込みを直接実行できず、その後の操作は通常の許可・安全規則に従う。バックグラウンドで継続する承認済みタスクも同じ。任意のタイミングで本カスタムMCPツールを無人呼出しできることは未確認。

第1段階: ユーザーが許可したDot会話から、Windows側の対応依頼なしで専用ツールを呼び出す。第2段階: ユーザーが明示的に許可した一回限りのスケジュール済みタスクから同ツールを呼び出し、新しいWindows発言なしに受信することを確認。第3段階: 日常運用の能動発言（承認済みタスク完了・必要な通知・定期的な確認など）の使い分けを検討。第1段階の成功を自律実行成功と同一視しない。プラットフォームの承認を回避しない。PC・Relay・Tunnelが停止中にクラウド側で永続キューイング・自動再配送される保証はない。擬似ユーザー発言や常時ハートビートによる強制起動は本候補に追加しない。

## 2. MCP契約

| ツール | 引数 | 役割 |
| --- | --- | --- |
| get_context | request_id | 既存依頼の前後関係（当該依頼まで） |
| reply | request_id, text | 既存依頼への最終回答 |
| send_message | channel_id, message_id, text | 対応する依頼のない独立した発言を保存 |

`send_message` は書き込みツールとして明示し、readOnlyHint=false、destructiveHint=false、idempotentHint=true、openWorldHint=false（接続済み所有者の単一会話へ追記）とする。既存プラグイン／トンネルのアクセス権、ローカルBearer認証を維持する。channel_idは既存ストアのチャンネル（通常 hagoromo-mini）と一致させる。channel_idは認証情報ではない。実際のクラウド側呼出し元がDotであることをサーバー側で独立証明するものではないため、従来どおり所有者が接続・承認する単一チャネルの信頼境界を利用する。他会話への送信権限の追加はM05で扱う。

例（UUIDは1発言につき1つ生成し、結果不明時に同じ値・本文で再試行する）:

```json
{
  "channel_id": "hagoromo-mini",
  "message_id": "3c22452f-ef62-4bfd-b4c3-b5afbd4ce991",
  "text": "能動発言のテストです。"
}
```

入力message_idは非ゼロGUIDのD形式。本文は既存と同じ上限32768 UTF-16コード単位、空白のみは拒否。未知の引数、他channelは-32602。ID・本文不正はtool error。成功は永続化済みであることを示し、表示・閲覧・既読を意味しない。戻り値は channel_id, message_id（正規化した入力UUID）, stored_message_id（proactive_を接頭辞とする履歴ID）, sequence, stored=true。同じID・同じ本文は既存の記録とsequenceを返す。異なる本文は競合エラーとする。別IDの同文発言は別発言。通信結果が不明の場合に新IDを作らない。実際のツール呼出しの承認とクラウド側メタデータ更新は別途利用者が確認する。

## 3. SQLite・履歴との整合

schema version 2、既存messages/outbox/subscriptionのテーブルと制約は変更しない。既存のmessages.request_idはNOT NULLの相関キーで、ユーザー依頼の外部キーではない（outbox側は外部キー）。能動発言の行は次の構造とする。

- id = proactive_ + 正規化したUUID
- role = assistant
- text = 発言本文
- in_reply_to = NULL
- request_id = id（能動発言専用の内部相関キー。実際のユーザー依頼を作らない）
- sequence と created_at は既存InsertMessageで確定する。

従来のユーザー依頼IDはGUID、回答IDはreply_ + GUID。能動発言の内部相関キーはproactive_名前空間で分離され、同一生UUIDでも衝突しない。outboxに行を作らず、message.createdを発行せず、送信待ち・返信待ち件数を変えない。既存の未回答要求を回答済み扱いにしない。購読登録を自動操作しない。GetContextは実ユーザー依頼だけを対象とし、当該依頼までの時系列に過去の能動発言を含める。Windowsから能動発言に続けて返信する場合は、既存のin_reply_toでassistantの履歴IDを参照できる。旧版は同じschemaを開けるが新ツール・自発ラベルを持たない。実データのロールバック／復旧にあたってDBを不用意に差し替えない。

## 4. DesktopとM09の調整境界

既存ChatMessageの公開フィールド、role、メッセージ一覧HTTP API、取得カーソルを変更しない。通常返信はrole=assistantでin_reply_toが実依頼ID、能動発言はrole=assistantでin_reply_to=nullかつidがproactive_名前空間。Desktopは既存の受信・永続新着ID・吹き出し処理で取り込む。履歴カードの見出しに「羽衣ちゃん・自発」を付加し区別する。新着解除はM14に委ね、今回の規則を変更しない。初回の既存履歴を全件新着にしない現行基準を維持し、永続チェックポイントがある再起動時の未読復元はそのまま扱う。

M09との連携案（M09担当との調整前の提案）:
- M04はMainWindow.Conversation.cs、MainWindow.Interaction.cs、MainWindow.xaml.csに書き込まない。受信経路の変更は不要であり、DesktopPanels.csの履歴カード見出しだけを変更する。M09がこれらの会話・マウス・キャラクター制御へ演出の接点を追加できる。
- イベントの入力は既存ChatMessage（シーケンス、ID、role、in_reply_to、時刻、本文）。通常返信・能動発言ともassistantの新規確定受信として扱える。演出側は本文依存を必須にせず、メッセージIDを重複排除キーとして使う。M09の送信演出はuser行の受理確定を基点とする。
- 既存ReadMessagesAsyncはページ読込を完了してからObserveReplies(firstCompleteRead)を呼ぶ。初回の履歴読込、既に表示したメッセージの再取得、履歴を開いた操作、未読解除で演出を再発生させない。初回読込を除く実受信の確定通知を、M09側で追加する案を推奨する。複数ページで途中失敗した場合は次の完全読込に持ち越し、失敗途中のカーソルと受信済みIDを混同しない。イベント名・引数型・実際のフックはM09担当と合意してから確定。
- M09の演出を受信処理の必須依存にしない。演出失敗で会話保存や受信を妨げない。各枝の独立検証後、main統合は直列化し、M04/M09結合時に通常返信と能動発言の両方で回帰確認する。M09の枝・成果には書き込まない。

## 5. 検証・残事項

[実機確認手順](../operations/M04-verification.md) を参照。追加の回帰4件（永続化・関連付け・キュー不変、入力・並行冪等性、MCP契約、Desktop新着基準）と従来31件を実行する。現候補は静的照合のみ。実際のビルド、Windows UI、クラウド側新ツール認識、Dotによる明示発言・承認済み一回タスク、再起動、M09連携は未確認。
