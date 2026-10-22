# Automatikus WebAPI-életciklus ellenőrzése – 2026-10-22

Ez a jelentés az automatikus Debug build, Development indítás és saját processzhez kötött leállítás ellenőrzését rögzíti. A [korábbi funkcionális](verification.md) és [diagnosztikai](diagnostics-verification.md) jelentés változatlan történeti bizonyíték.

## Eredmények

| Ellenőrzés | Eredmény |
| --- | --- |
| LiveTests Debug build | Sikeres, 0 figyelmeztetés, 0 hiba |
| Végleges Demo WebAPI Debug build, helyi projekthivatkozásokkal és szekvenciális MSBuilddel | Sikeres, 0 figyelmeztetés, 0 hiba |
| LiveTests élő mód nélkül | 39 sikeres determinisztikus eset, 7 kihagyott élő eset |
| Teljes Core regresszió | 259/259 sikeres |
| Explicit Duende a más providert választó JSON, környezet és User Secrets ellenében | Determinisztikus tesztek sikeresek, a WebAPI Duende-tárolója és a BlazorApp projektazonosítójú tárolója kiválasztva |
| Konfigurációs fájlok változatlansága | A tesztben létrehozott JSON- és titokfájlok bájtszinten változatlanok; az alkalmazás JSON- és User Secrets-fájljait a megvalósítás nem írja |
| Foglalt port | Build és alkalmazásindítás előtt hiba, a meglévő listener tovább elérhető |
| Sikertelen build | A nem nulla exit kód hibát okoz; TargetDir-feloldás és alkalmazásindítás nem történik |
| Készenléti védelem | Saját futásazonosító, Duende-provider és `recrobyEnabled=true` szükséges; idegen példány, eltérő provider és kikapcsolt Recroby elutasítva |
| Készenléti timeout és korai kilépés | Külön, egyértelmű hibák a determinisztikus tesztekben |
| Leállítás | Saját valódi helyi tesztprocessz normál befejezés és inicializálási hiba után megszűnik; a bemenetet figyelmen kívül hagyó processz a korlátozott várakozás után kényszerítetten leáll |
| Másik processz védelme | A külön létrehozott, nem a hosthoz tartozó processz érintetlen marad |

## Első élő kísérlet: LocalDB-példánynév-feloldási hiba

A futtatás előtt a Duende a 11900-as porton, PID 26880 alatt futott; a WebAPI 11913-as portja szabad volt. A teljes LiveTests projekt `RGF_DEMO_LIVE_LLM=1` mellett, Debug konfigurációval indult, előzetes kézi WebAPI-build és -indítás nélkül.

A fixture automatikusan lefordította a WebAPI-t, feloldotta a Debug TargetDir könyvtárát, majd elindította a DLL-t. A saját WebAPI PID-je 40640 volt. Az alkalmazás az adatbázis-inicializálás során, a HTTPS-készenlét előtt kilépett, `Microsoft.Data.SqlClient.SqlException` és `System.ComponentModel.Win32Exception` hibával. A tesztkimenet szűrt kivételtípusokat tartalmazott, titkokat nem.

A futás eredménye 37 sikeres determinisztikus teszt és 7 fixture-inicializálási hiba volt. Ez az új leállítási és buildbeállítási esetek hozzáadása előtti élő kísérlet; a végleges determinisztikus eredmény a fenti 39 sikeres eset. A saját WebAPI kilépési kódja -532462766 volt; a host megvárta a kilépést és felszabadította a processz erőforrásait. Utána a 11913-as porton nem futott listener, a Duende változatlanul PID 26880 alatt, a 11900-as porton futott.

A valós Duende-bejelentkezés, a védett futó provider-/modellkatalógus, a normalizált/strukturált LLM-diagnosztika és a hét élő eset teljes végrehajtása ebben a kísérletben nem ellenőrizhető: a WebAPI nem jutott készenléti állapotba. A további vizsgálat igazolta, hogy az adatbázis elérhető volt; az alábbi környezeti eltérés okozta a hibát. Az adatbázis leállására vonatkozó korábbi következtetés nem volt helyes.

Az ismert három Entity-/Property-felsorolási hiba állapotáról ez a kísérlet nem ad új LLM-eredményt. Az érintett hét élő teszt állításai, a metadata-teljesség és a ConversationId/WorkflowRunId szerinti naplókorreláció változatlanok. Az adatbázist és a Duende IDP-t a host nem indította és nem állította le.

## A LocalDB-eltérés pontosítása

A meglévő Development-konfiguráció `SQLServerDev` kapcsolatot, `(localdb)\mssqllocaldb` szervert és `RGF.Demo10` adatbázist választott. A Duende titoktároló elérhető volt; az adatbáziskonfiguráció betöltése nem maradt ki.

A futtatási környezetben a `SqlLocalDB info MSSQLLocalDB` állapota `Stopped` volt, miközben a 5464 PID alatt már futott a megfelelő LocalDB-processz. A futó processz és saját SQL-naplója `LOCALDB#4EC8F72E` csatornát azonosított, a névfeloldásnál olvasott registrybejegyzés viszont `LOCALDB#E2D75354` példányt tartalmazott. Ez a megfigyelt Windows-futtatási kontextus eltérése; nem az adatbázis elérhetetlenségének bizonyítéka.

A már futó LocalDB saját named pipe csatornáján, Windows-hitelesítéssel végzett `SELECT DB_NAME()` sikeresen `RGF.Demo10` értéket adott vissza. Nem indítottunk és nem állítottunk le adatbázist, nem hoztunk létre példányt, és nem módosítottunk registrybejegyzést.

A további élő ellenőrzésben csak a tesztet indító `ProcessStartInfo.Environment` kapott `ConnectionStrings__SQLServerDev` felülírást, ugyanennek az adatbázisnak a működő named pipe címével. A beállítások többi része a meglévő konfigurációból és User Secretsből származott; az AI-provider és modell változatlan maradt. A JSON- és User Secrets-fájlokat nem írtuk át. A változó nem került a szülőprocessz vagy a gép globális környezetébe, és a változó csatornanevet nem építettük be a host megvalósításába.

A host az ilyen LocalDB-indítási hibát most titkoktól mentes, külön magyarázattal jelzi; a determinisztikus teszt ellenőrzi, hogy egy ugyanabban a forrássorban szereplő tesztjelszó nem kerül át a hibaüzenetbe. Ez diagnosztikai pontosítás, nem automatikus adatbázisindítás vagy kapcsolatátállítás.

Az eredeti, kizárólag `RGF_DEMO_LIVE_LLM=1` változóval kiadott parancs ebben az izolált futtatási kontextusban továbbra is a hibás LocalDB-névfeloldásba ütközik. Az alábbi sikeres életciklus-ellenőrzést ezért a futtatásra korlátozott csatornafelülírással kell értelmezni; nem igazolja az eredeti parancs változtatás nélküli működését ebben a kontextusban.

## Dokumentált teljes élő futás a működő adatbázis-csatornával

A teljes Debug tesztparancs `--output Detailed --report-xunit-trx --report-xunit-trx-filename lifecycle-live.trx` kapcsolókkal, a fenti, kizárólag tesztprocesszre korlátozott adatbázis-csatornafelülírással futott. A hét élő eset állításai változatlanok maradtak. Az összesített eredmény **46 eset: 43 sikeres, 3 hibás, 0 kihagyott**, körülbelül 95 másodperc alatt. A három hiba miatt a tesztrunner kilépési kódja 2.

| Ellenőrzés | Tényleges eredmény |
| --- | --- |
| Automatikus WebAPI-build | Sikeres, Debug, helyi projekthivatkozások, szekvenciális MSBuild |
| Saját WebAPI indítása | PID 16808, `bf256a7f5e17455e916844b6ed2c2d27` futásazonosító |
| HTTPS-készenlét és provider | Saját futásazonosító, `recrobyEnabled=true`, effektív `Duende`; a TLS-ellenőrzés bekapcsolva maradt |
| Valódi Duende-bejelentkezés és védett provider-/modellkatalógus | Sikeres |
| Valódi AI-konfiguráció | `OpenAI`, `gpt-6-luna`, meglévő `Duende` User Secrets; nem használtunk helyettesítő modellt |
| Diagnosztika | Normalizált és strukturált tartalom engedélyezve, RAW kikapcsolva; a smoke és a sikeres metadata-követő eset meglévő diagnosztikai állításai teljesültek |
| Chat-smoke, általános chat és Customer-kulcs követő kérdése | Sikeres |
| Közvetlen Entity-felsorolás | Válaszminőségi hiba: mind a 13 jogosult technikai név hiányzott a felhasználói válaszból |
| Eredeti kétfordulós Entity-beszélgetés | Válaszminőségi hiba: mindkét válaszból hiányzott mind a 13 jogosult technikai név |
| Customer Property-felsorolás | Válaszminőségi hiba: mind a 11 elvárt Property-név hiányzott |
| Automatikus WebAPI-leállítás | PID 16808 szabályosan kilépett, exit=0; a 11913-as port utána szabad |
| Külső szolgáltatások érintetlensége | Duende továbbra is PID 26880, port 11900; LocalDB továbbra is PID 5464 |

A gépi jelentés a helyi, Git által kizárt `TestResults/lifecycle-live.trx` fájlban található. A futás előtti és utáni port-/processzellenőrzések külön is igazolták a saját WebAPI leállását és az IDP/adatbázis érintetlenségét. A három ismert válaszminőségi hiba reprodukálódott; javításukat nem végeztük el, ellenőrzéseiket nem gyengítettük.

A kimenet nélküli előző, ugyanezzel az adatbázis-csatornával futó kísérlet szintén elérte a készenlétet (PID 33820), majd leállt. Annak hiányzó tesztkimenete miatt a pontos eredményeket kizárólag a fenti, TRX-szel dokumentált futáshoz rendeljük.
