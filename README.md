# Kirihari（切り貼り）— 動画の連結とカット専用アプリ

実行ファイルのダウンロード.
https://github.com/k518-2026/Kirihari/releases

ビデオカメラは1ファイル 4GB までしか記録できないため、長く撮ると動画が何本にも分かれます。
Kirihari は、**それをつなげて、いらない所を切って、YouTube 用の MP4 にする**ことだけに絞った Windows アプリです。
高度な編集ソフトを立ち上げる必要はありません。WPF（C# / .NET 8）製で、外部の NuGet には依存しません。

| | 向いている人 | できること |
|---|---|---|
| **Kirihari Lite**（`Kirihari_Lite.exe`） | はじめて使う人 | 手順は「入れる → 切る → 書き出す」の3つだけ。形式の判断は自動 |
| **Kirihari Pro**（`Kirihari_Pro.exe`） | 細かく調整したい人 | Lite の全機能に加えて、時刻の直接入力、区間の数字での調整、10コマ送り、再生速度、画質・解像度・フレームレート・インターレース・音質・エンコーダーの指定 |

2つは同じ部品（`src\Kirihari.Core`）を使っているので、書き出しの結果は同じ品質です。

## 読み込める形式

カムコーダーの形式を中心に、ffmpeg が読めるものを入れられます。
`MTS` `M2TS` `M2T`（AVCHD・HDV）、`MOD` `TOD`、`MP4` `MOV` `M4V`、`MPG` `VOB`、`AVI` `DV`、`MKV` `WMV` `TS` `3GP` `MXF` `FLV` `WEBM`。
ファイルはドラッグ＆ドロップで入れます。フォルダーごと（AVCHD の `STREAM` フォルダーなど）も可能で、中の動画を名前の順に読み込みます。
**出力は MP4（H.264 / AAC、または映像そのまま）だけ**です。

## 使い方（Lite）

1. 動画をウィンドウにドロップ（または［＋動画を入れる］）。ファイル名の順につながります
2. 再生位置を動かして、
   - **［ここで分ける］**で区間を分け、［この区間を消す］
   - **［ここより前／後ろを全部消す］**で頭やお尻を落とす
   - 消したい範囲の始まりで［はじめ］、終わりで［おわり］を押して［範囲を消す］
3. **［MP4で書き出す］**

間違えたら［元に戻す］（Ctrl+Z）。

### ショートカット

`Space` 再生 / `S` 分割 / `I` `O` 開始点・終了点 / `X` 範囲削除 / `Del` 区間削除 / `←` `→` 1コマ / `Shift+←→` 10コマ（Pro）/
`Ctrl+←→` 前後の区間 / `Home` `End` 先頭・最後 / `Ctrl+Z` `Ctrl+Y` 元に戻す・やり直す / `Ctrl+O` 追加 / `Ctrl+E` 書き出し /
タイムライン上で `Ctrl+ホイール` 拡大、ホイールで左右に移動。

## 書き出しの2つの方法

| 方法 | 速さ・画質 | 切る位置 |
|---|---|---|
| **再エンコードなし** | とても速い（ほぼファイルのコピー速度）。画質は元のまま | 直前のキーフレームに合う（カメラによりおよそ 0.5〜2 秒おき） |
| **再エンコード** | 時間がかかる（GPU があれば速い） | 1コマ単位で正確。形式がバラバラの動画もそろえてつなげる |

**既定は「再エンコードなし」**です。次の場合は、自動で再エンコードに切り替わり、理由が画面に出ます。

- つなぐ動画の画面サイズ・フレームレート・コーデック・音声形式が違う
- 映像が MPEG-2 や DV など、MP4 にそのまま入れられない形式
- （Pro）画面サイズ・フレームレートを変える、インターレースを「常に解除」にした

音声が AC-3（Dolby Digital）などのときは、映像はそのままで**音声だけ AAC に作り直します**（軽い処理です）。
再エンコード時、インターレースの映像（1080i）は自動で解除します。GPU（NVIDIA / Intel / AMD）は起動時に実際に試し、使えるものだけを使います。GPU で失敗したら自動で CPU にやり直します。

書き出しの途中は `○○.mp4.part` に書き、最後まで成功したときだけ本来の名前にします。中止・失敗では残しません。
終わったら、出力ファイルの長さを調べて予定と比べ、結果に表示します。

## ffmpeg について（必要です）

動画の読み込みと書き出しは [FFmpeg](https://ffmpeg.org/) を使います。アプリには同梱していません。
次のどれかで見つかれば、自動で使います。

1. アプリと同じフォルダー（または `tools\`・`ffmpeg\`）に `ffmpeg.exe` と `ffprobe.exe` を置く
2. すでに PATH が通っている（`winget install Gyan.FFmpeg` で入れた場合も含む）
3. 画面の［ffmpeg.exe を選ぶ…］で場所を指定（次回以降も覚えます）

配布するときに ffmpeg を同梱する場合は、ffmpeg のライセンス（ビルドにより GPL / LGPL）の表示が必要です。
このリポジトリと配布 ZIP には ffmpeg は含まれていません（Kirihari 本体は MIT ライセンスです）。

## 注意

実際のカムコーダーの録画ファイルすべてで動作を確かめたわけではありません。
大切な動画は、元のファイルを残したまま使ってください（Kirihari は元のファイルを書き換えません）。
書き出したあとは、再生して内容を確かめてください。

## ビルド

```
dotnet build Kirihari.slnx -c Release
```

Visual Studio 2022（17.8 以降）なら `Kirihari.slnx` を開いて F5。.NET 8 SDK 以降が必要です。

単一 exe（ランタイム同梱・約 68 MB）にするとき：

```
dotnet publish src\Kirihari.Lite\Kirihari.Lite.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\Lite
dotnet publish src\Kirihari.Pro\Kirihari.Pro.csproj  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\Pro
```

アイコンは `assets\make_icons.ps1` で作り直せます。

## 構成

```
src\Kirihari.Core\   共通部品（編集内容 EditList、ffmpeg の組み立て ExportPlanner、タイムライン、プレビュー、ViewModel、配色）
src\Kirihari.Lite\   かんたん版の画面
src\Kirihari.Pro\    細かく調整できる版の画面
tests\Kirihari.Tests\ 画面なしの検査と、画面の描画確認
```

- 編集内容は「元ファイルの何秒〜何秒」という区間を並べたもの。切る・消す・並べ替えはすべて区間の組み替えで、元の動画は変更しません。
- 再エンコードなしは ffmpeg の concat デマクサー（`inpoint` / `outpoint`）で、TS 系の先頭時刻のずれも補正して書き出します。
- 再エンコードは区間を別々の入力として開き、各区間を**コマ数ちょうど**にそろえてから連結します（切った数だけ映像と音声がずれるのを防ぐため）。

## 検査

```
dotnet run --project tests\Kirihari.Tests -c Release
```

編集計算・形式判定・時刻入力・書き出し設定と、ffmpeg で実際に書き出す検査（再エンコードなし／再エンコード／音声なし混在／Pro 設定／中止／区間80個／GPU）が走ります。
試験用の動画は ffmpeg で自動生成します。

画面の描画確認（Lite / Pro の画面に動画を読み込んで PNG に描く）：

```
dotnet run --project tests\Kirihari.Tests -c Release -- shot lite <動画フォルダー> <出力.png>
```

## 確認できていないこと

- 実際のカムコーダーの録画ファイル（AVCHD / HDV / DV など）での確認は未実施です。検査には ffmpeg で作った MTS 風の試験動画を使っています。
- プレビュー再生は Windows の再生機能（MediaElement）に頼ります。再生できない形式は、止まった画面の確認だけになります（書き出しには影響しません）。
- マウス操作・ドラッグ＆ドロップ・書き出しダイアログなど、画面を人が操作する部分は、起動と描画の確認までで、操作の確認は未実施です。
