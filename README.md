# visor-app-monitor

[visor](https://github.com/MoomA-0750/visor)（VR の中で Claude と話すための SteamVR のオーバーレイ）のアプリ「リソース」。

- CPU・GPU・メモリ・ビデオメモリ・VR のコマの時間と、重いプロセスを並べる。窓が見えている間だけ 1 秒おきに取る
- プロセスの × を 2 回押すと、そのプロセスを終える
- Claude に道具 `system_status`（PC の様子）と `end_process`（プロセスを終える）を渡す

## 入れ方

visor のホームの「アプリを探す」から入れる（visor の公式のカタログに載っている）。

## 作り方

- .NET 9 の C#。visor との受け渡しは `VisorApp.cs`（visor の `sdk/dotnet/VisorApp.cs` の写し）
- `dotnet publish -c Release -o bin` で組み立てる。visor はリリースに `visor-app.zip` があればそれを、無ければソースを取って `app.json` の `build` で組み立てる
- `v0.1.0` のようなタグを push すると、GitHub Actions がリリースを作る
