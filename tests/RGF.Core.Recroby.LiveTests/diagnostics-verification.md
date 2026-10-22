# Beépített diagnosztika ellenőrzése – 2026-10-22

Ez a jelentés a Recrovit.AI `36d53ac` commitjára épülő NLog-integráció tényleges ellenőrzéseit rögzíti. A [verification.md](verification.md) korábbi eredményei változatlan történeti bizonyítékok; az alábbiak külön futtatásokból származnak.

## Végleges eredmény

| Ellenőrzés | Tényleges eredmény |
| --- | --- |
| Demo WebAPI Debug build, szekvenciális helyi projekthivatkozások | Sikeres; 0 hiba, 4 NuGet-vulnerability-adatlekérési figyelmeztetés |
| Végleges LiveTests Debug build | Sikeres; 0 figyelmeztetés, 0 hiba |
| Meglévő Core regressziós csomag | 259/259 sikeres |
| LiveTests élő engedély nélkül | 22 determinisztikus sikeres, 7 élő eset kihagyva; nincs hitelesítés/LLM-hívás |
| Végleges teljes LiveTests, valódi LLM-mel | 29 eset: 26 sikeres, 3 hibás, 0 kihagyott |
| Duende bejelentkezés és védett provider-/modellkatalógus | Sikeres |
| Valós provider-smoke | Sikeres: `15`, konfigurált normalizált modell, pozitív tokenhasználat, chat agent |
| Általános chatkontroll | Sikeres |
| Customer elsődleges kulcsának követő kérdése | Sikeres: `CustomerId`, tényleges jogosult metadata és korábbi kérdéselőzmény |
| Közvetlen teljes Entity-felsorolás | Hibás: mind a 13 tényleges technikai név hiányzott a felhasználói válaszból |
| Eredeti kétfordulós Entity-beszélgetés | Hibás: mindkét felhasználói válaszból mind a 13 technikai név hiányzott |
| Customer Property-felsorolás | Hibás: mind a 11 tényleges Property-név hiányzott |
| Meglévő Recrovit.AI `BuiltInDiagnosticsTests` | 26/26 sikeres: kapcsolók, Trace-szint, RAW opt-in, strukturált tartalomvédelem és korreláció |
| Demo NLog offline próba a tényleges XML-konfigurációval és beépített sinkkel | Sikeres: kikapcsolt normalizált tartalom hiányzik, bekapcsolva JSON, korreláció, külön strukturált esemény, nincs ismételt forgalmi rekord vagy próbatartalom a basic logban |
| Végleges futás központi naplója | 25 normalizált kérés és 25 válasz; 0 ismétlődő kérés-InvocationId; 0 RAW esemény |
| Naplófájl elérhetősége | Git által kizárt; `/logs/ai/events.jsonl` és `/bin/Debug/net10.0/logs/ai/events.jsonl` HTTP 404 |
| Standard napló tartalomellenőrzése | Az aktuális vizsgálati időszakban nincs normalizált/strukturált teljes tartalomra utaló vizsgált marker |

A végleges futás 2026-10-22-én, budapesti idő szerint körülbelül 22:38–22:39 között történt, OpenAI / `Luna-6` / `gpt-6-luna`, meglévő `Duende` User Secrets, Debug és Development mellett. A szolgáltató konfigurációját nem írtuk felül. A RAW végig kikapcsolt maradt. A provider aliasát a futó védett katalógus, a logikai modellazonosítót és a használatot a normalizált események igazolják; fizikai HTTP-modellmezőre vagy HTTP-attemptszámra nem teszünk állítást.

## Végleges futás bizonyítékai

A központi napló a Demo Debug `TargetDir` alatti `logs/ai/events.jsonl` fájl. A következő WorkflowRunId-k a beépített TrafficContent és StructuredContent eseményekből is visszakereshetők:

| Eset | WorkflowRunId |
| --- | --- |
| Közvetlen Entity-lista | `74181f2892a44d2691492f27a5629347` |
| Chatkontroll | `3291e4012ef1493a932ada0697137320` |
| Eredeti első kérdés | `3782cb3ee046496eae111c24acba58b4` |
| „Sorold fel őket.” | `e9976d5ca8f249289d672ea7f10c54e2` |
| Property-felsorolás | `a13f31ad482d4286a66daa7615f81936` |
| Kulcskérdés előkészítő fordulója | `dace0abdd085431e87fb91be105a9d6c` |
| Elsődleges kulcs követő kérdése | `cdfbcff31b744b00a4aff9501ac6a8d0` |
| Provider-smoke | `246acfaba5444e29b1df230e21656800` |

A katalógus referencia tényleges, szűretlen tooleredmény: 13 Entity, TotalCount=13, NextOffset=null. A Customer részletes metadata 11 Property-t tartalmaz. A tesztkód nem égeti be az Entity-neveket vagy ezt a darabszámot. A tényleges toolhívásokat a provider-válasz FunctionCall neve és a következő kérés azonos CallId értékű FunctionResult.Json tartalma kapcsolja össze. A beépített invoker strukturált eseménye külön mutatja a feldolgozott kimenetet. A kétfordulós teszt mindkét eredeti kérdést elküldi, és mindkét hiányos választ jelenti.

A normalizált kérések számlálása logikai providerinvokációkat mutat. Nem azonos a fizikai HTTP-attemptök számával. A diagnosztika nem regisztrál új guardot, sinket, callbacket vagy metadataolvasást; a napló újraolvasása nem küld új LLM-kérést. A valódi kérések változatlan Duende-, jogosultság- és Credit-folyamaton mentek át. Külön adatbázis-szintű Credit-egyenlegvizsgálat nem történt.

## Környezeti és köztes eredmények

Az első sandboxos build a meglévő obj/bin hozzáférésén bukott el; a hozzáférést biztosító újrafuttatás sikerült. Az első saját WebAPI-indítás és egy későbbi elkülönítési próbálkozás LocalDB-indítási hibával leállt. A fejlesztő a normál helyi környezetet elindította; az élő tesztek ezt használták. Az első smoke még nem érte el a backendet; később a valódi chatválasz már helyes volt.

Az első naplócél LocalApplicationData volt. A fejlesztői WebAPI ott 338 KB-os naplót írt, de a Codex fájlnézete ugyanazon az útvonalon csak a saját 4976 bájtos offline próbafájlját látta. Egy köztes teljes futás ezért 23 sikeres és 6 diagnosztikai hibás esetet adott; ebből nem következtettünk válaszminőségre. A végleges `${basedir}` útvonal a megosztott, Git által kizárt buildkimenetbe ír. A teszt a tényleges TargetDir projektértékelésével ugyanazt oldja fel, és a célzott smoke ezt követően sikerült.

Az első működő naplós metadatafutás 1 sikeres és 4 hibás esetet adott: a két felsorolási hiba mellett a követő ellenőrzések tévesen megkövetelték a régi tooleredményt a historyban akkor is, amikor friss olvasás történt. Ezt a tesztfeltételt javítottuk. A végleges teljes futásban már csak a három eredeti válaszminőségi hiba maradt. Újrafuttatás oka tényleges infrastruktúra- vagy tesztfeltétel-javítás volt; puszta naplókésésre nem történt automatikus új LLM-kérés.

A helyi Development-konfiguráció ideiglenes diagnosztikai felülírását a végén pontosan visszaállítottuk. Normál indításban a tartalomrögzítés kikapcsolt, a RAW kikapcsolt. A már elkészült teljes tartalmú naplók helyben megmaradnak; ezek nem verziókezeltek és nem HTTP-n kiszolgált fájlok.

## Futtatott parancsok

Minden build és teszt szekvenciális, Debug konfigurációjú volt. A projektgyökerekből:

```powershell
# rgf-demo
dotnet build src/RGFDemo.WebAPI/RGFDemo.WebAPI.csproj -c Debug -m:1 -p:BuildInParallel=false

# rgf-core
dotnet build tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug -m:1 -p:BuildInParallel=false
# Futó WebAPI mellett a teszt újrafordításai a meglévő függőségeket használták:
dotnet build tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug -m:1 -p:BuildInParallel=false -p:BuildProjectReferences=false

dotnet test --project tests/RGF.Core.Tests/RGF.Core.Tests.csproj -c Debug --no-build
# Élő mód nélkül, majd RGF_DEMO_LIVE_LLM=1 mellett:
dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build
# Külön smoke és metadatafutások is történtek --filter-class szűrővel.

# recrovit-ai
dotnet build tests/Recrovit.AI.TestApp.Tests/Recrovit.AI.TestApp.Tests.csproj -c Debug -m:1 -p:BuildInParallel=false
dotnet test --project tests/Recrovit.AI.TestApp.Tests/Recrovit.AI.TestApp.Tests.csproj -c Debug --no-build --filter-class Recrovit.AI.TestApp.Tests.BuiltInDiagnosticsTests
```

A Core regressziós csomag a sikeres futás előtt Debugban lefordult. Az első `dotnet test ... -m:1` parancsban az MTP runner tévesen tesztkapcsolóként kapta az `--m` értéket, így azzal nulla teszt futott; a fenti támogatott `--project ... --no-build` parancs 259/259 esete a tényleges eredmény.

Nem készült új teszteset vagy új diagnosztikai szolgáltatás. Nem kellett Recrovit.AI-forrásmódosítás. A Recroby workflow-ja, az Output/UserMessage contract, a hitelesítés és a Credit-kezelés változatlan. Az eredeti Entity-/Property-felsorolási hiba továbbra is kimutatható.
