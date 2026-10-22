# RGF.Core.Recroby.LiveTests – valós LLM-integrációs tesztek

A Core repóhoz tartozó tesztprojekt azt ellenőrzi, hogy a Recroby a tényleges RGFDemo alkalmazásban, valódi felhasználói bejelentkezéssel, adatbázissal és LLM-szolgáltatóval megfelelően működik-e. A már futó WebAPI-t hívja; nem helyettesíti a modellt vagy a jogosultságkezelést tesztváltozattal.

A projekt kétféleképpen használható:

- **Élő szolgáltatások nélkül:** a konfigurációbetöltést és a bejelentkezési protokoll egyes szabályait ellenőrző determinisztikus tesztekhez.
- **Valós integrációként:** a Duende-bejelentkezés, az LLM-hívások, az Entity-/Property-felsorolások és a beszélgetés folytatásának ellenőrzéséhez. Az LLM-et használó esetek a beállított szolgáltatóhoz küldenek kéréseket, és a szokásos RGF Credit-elszámolással futnak.

## Mit ellenőriznek a tesztek?

| Tesztcsoport | Ellenőrzés |
| --- | --- |
| Konfigurációs és protokolltesztek | A JSON-beállítások, környezeti változók és User Secrets sorrendje; kliensbeállítások; OIDC-visszahívás ellenőrzése. Nem igényelnek hálózatot vagy belépési adatokat. |
| Bejelentkezési teszt | Valódi Duende-belépés a BlazorApp kliensével, majd a WebAPI védett provider-/modellkatalógusának lekérése. Nem hív LLM-et és nem fogyaszt AI Creditet. |
| LLM smoke teszt | Egyszerű kérdés megválaszolása a beállított modellel. A válaszon túl ellenőrzi a tényleges providerforgalmat, a modellt és a tokenhasználatot. |
| Entity-beszélgetési tesztek | Kétfordulós és közvetlen Entity-felsorolás, Customer Property-lista, elsődleges kulcsra vonatkozó követő kérdés, valamint általános chatkontroll. |

A felsorolásoknál nem elegendő egy sikeres HTTP-válasz vagy a találatok darabszáma. A válasznak tartalmaznia kell a felhasználó számára ténylegesen elérhető technikai neveket, a metadata tooloknak pedig valóban le kell futniuk. Az elvárt Entity-lista a jogosultságszűrt katalógusból származik; nincs beégetett 13-as elvárás.

A Customer-esetek a demóban használt `RgCustomer` Entity-re és a `CustomerId` Property-re épülnek. A követő teszt a kulcsra vonatkozó metadata-leírást, a tényleges tooleredményt és a korábbi kérdés beszélgetési előzményeit is ellenőrzi.

## Előfeltételek

- .NET 10 SDK és az aktuális forráskód a kapcsolódó helyi RGF-projektekkel.
- **Debug** konfiguráció: ebben érvényesülnek a helyi projekthivatkozások.
- A WebAPI **Development** környezetben fusson. Ez a Debug konfigurációtól külön beállítás.
- Élő futtatáshoz elérhető adatbázis, futó Duende szerver és HTTPS-en elérhető RGFDemo.WebAPI szükséges.
- A lokális Duende környezetben működő `Admin` / `rgfdemo` demóbelépés, a tesztelt Entity-khez megfelelő jogosultságokkal és az LLM-hívásokhoz szükséges Credit-hozzáféréssel.
- A WebAPI-ban beállított modell támogassa a natív tool callingot és a strukturált végső választ.

A tesztek nem indítják el és nem állítják le az adatbázist, a Duende szervert vagy a WebAPI-t. A BlazorApp felületét nem kell elindítani, de a benne beállított OIDC-kliensnek és visszahívási címnek érvényesnek kell lennie a Duende szerveren.

A projekt xUnit v3-at és Microsoft.Testing.Platformot használ. A tesztprojekt parancsait az **rgf-core repository gyökérkönyvtárából** futtasd. A WebAPI fordítása és indítása külön, az **rgf-demo gyökeréből** történik.

## 1. Fordítás

A Core gyökeréből fordítsd le a tesztprojektet és a hivatkozott Core komponenseket. A projekt nem hivatkozik a Demo alkalmazásprojektjeire:

```powershell
dotnet build tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug -m:1
```

A build-, restore- és tesztparancsokat egymás után futtasd, ne párhuzamosan. A már futó WebAPI zárolhatja a kimeneti DLL-eket; újrafordítás előtt ilyenkor állítsd le. A további tesztparancsok a már elkészült buildet használják a `--no-build` kapcsolóval.

## 2. Futtatás élő szolgáltatások nélkül

Ha csak a determinisztikus ellenőrzéseket szeretnéd futtatni, ne legyen bekapcsolva az élő mód:

```powershell
Remove-Item Env:RGF_DEMO_LIVE_LLM -ErrorAction SilentlyContinue
dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build
```

Ekkor nem történik bejelentkezés vagy LLM-hívás. Az élő tesztek `Skipped` állapotúak lesznek; ez nem jelenti azt, hogy az élő integráció sikeres.

## 3. A WebAPI indítása élő teszteléshez

Indítsd el az adatbázist és a Duende szervert. Egy külön terminálban, az **rgf-demo gyökeréből**, először fordítsd le, majd indítsd el a WebAPI-t. A tesztprojekt fordítása nem fordítja le a Demo WebAPI-t:

```powershell
dotnet build src/RGFDemo.WebAPI/RGFDemo.WebAPI.csproj -c Debug -m:1

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Recrovit__AI__Diagnostics__Normalized__Enabled = 'true'
$env:Recrovit__AI__Diagnostics__Normalized__IncludeContent = 'true'
$env:Recrovit__AI__Diagnostics__StructuredOutput__IncludeContent = 'true'
$env:Recrovit__AI__Diagnostics__RawHttp__Enabled = 'false'
dotnet run --project src/RGFDemo.WebAPI/RGFDemo.WebAPI.csproj -c Debug --no-build --launch-profile https
```

Visual Studióból is indíthatod a meglévő HTTPS profillal. Ugyanúgy Debug konfigurációt, Development környezetet és a fenti, explicit diagnosztikai beállításokat használj. A normál HTTPS profil nem kapcsolja be a tartalomrögzítést. A változók módosítása után indítsd újra az alkalmazást.

HTTPS-tanúsítványhiba esetén ellenőrizd a fejlesztői tanúsítvány megbízhatóságát:

```powershell
dotnet dev-certs https --trust
```

A teszt nem kapcsolja ki a TLS-ellenőrzést.

## 4. Élő tesztek futtatása

A tesztprojekt a lokális Duende környezet nyilvános demóbelépését használja, rögzítetten:

- Felhasználónév: `Admin`
- Jelszó: `rgfdemo`

A belépési adatokat nem kell megadni, és környezeti változóval nem írhatók felül. Az `RGF_DEMO_USERNAME` és `RGF_DEMO_PASSWORD` változót a projekt már nem olvassa.

Az élő tesztek indításához csak az explicit engedélyezést kell beállítani a tesztet futtató PowerShell-folyamatban:

```powershell
$env:RGF_DEMO_LIVE_LLM = '1'

try {
    dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build
}
finally {
    Remove-Item Env:RGF_DEMO_LIVE_LLM -ErrorAction SilentlyContinue
}
```

A teszt automatikusan végzi a Duende authorization-code + PKCE bejelentkezést. Nem kell kézzel bearer tokent előállítanod. A megszerzett token csak memóriában marad.

Az automatizált belépés jelenleg a Duende normál bejelentkezési űrlapját és a beállított `ClientSecretPost` klienshitelesítést támogatja. Más identitásszolgáltató, MFA vagy külön hozzájárulási képernyő esetén a teszt egyértelmű hibával leáll.

### Egyetlen tesztcsoport kiválasztása

A fenti példa `dotnet test` sorát cseréld az alábbi parancsok egyikére. Az `RGF_DEMO_LIVE_LLM=1` engedélyezés ezekhez is szükséges.

Csak a bejelentkezés és a védett backendkatalógus ellenőrzése, LLM-hívás nélkül:

```powershell
dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build --filter-class RGF.Core.Recroby.LiveTests.RecrobyLiveAuthenticationTests
```

Csak az alapvető, valódi LLM-hívás ellenőrzése:

```powershell
dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build --filter-class RGF.Core.Recroby.LiveTests.RecrobyLiveSmokeTests
```

Csak az Entity-/Property-beszélgetések ellenőrzése:

```powershell
dotnet test --project tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj -c Debug --no-build --filter-class RGF.Core.Recroby.LiveTests.RecrobyLiveMetadataTests
```

Az élő tesztek közös fixture-t használnak és egymás után futnak. A fizetős kéréseket ne ismételd automatikusan pusztán azért, hogy egy futás sikeres legyen.

## A Demo checkout helye és az átállás

Az alapértelmezett élő környezet az `rgf-core` repóval szomszédos `rgf-demo` könyvtár. Eltérő elrendezésnél a tesztet indító folyamatban abszolút útvonalat adj meg:

```powershell
$env:RGF_RECROBY_DEMO_ROOT = 'C:\Work\Recrovit\rgf-demo'
```

A megadott útvonal elsőbbséget élvez; hibája esetén nincs visszaesés másik checkout használatára. A teszt még bejelentkezés előtt ellenőrzi a WebAPI és a BlazorApp alapkonfigurációját, a BlazorApp projektfájlját és launch-beállításait. Kikapcsolt élő mód esetén nincs szükség Demo checkoutra.

A korábbi `RGFDemo.WebAPI.LiveTests` projekt helyett a `tests/RGF.Core.Recroby.LiveTests/RGF.Core.Recroby.LiveTests.csproj` projektet használd az `rgf-core` gyökeréből. A tesztosztályok névtere `RGF.Core.Recroby.LiveTests`; az osztályszűrőket ennek megfelelően kell módosítani. Az `RGF_DEMO_LIVE_LLM` kapcsoló megmaradt. Az egyedi diagnosztikai kapcsoló megszűnt; a beépített Diagnostics konfigurációt használd. A felhasználónév és jelszó környezeti változói megszűntek; a teszt a fent megadott nyilvános lokális demóbelépést használja.

A tesztek tulajdonosa a Core, az élő host továbbra is az RGFDemo: saját adatbázisával, Duende-konfigurációjával, `RgCustomer` tesztadataival és NLog-konfigurációjával. Az áthelyezés nem változtatja meg a Recroby működését vagy HTTP-szerződését.

## Honnan származnak a beállítások?

A tesztprojekt a meglévő alkalmazásbeállításokat használja:

- **WebAPI:** `appsettings.json`, `appsettings.Development.json`, környezeti változók, majd a `JwtBearerOptions:Provider` által kiválasztott tároló betöltése a `builder.Configuration.AddUserSecrets(provider, reloadOnChange: true)` hívásnak megfelelően. Innen származik az AI-provider és az alapértelmezett modell; ez a User Secrets-betöltés Duende esetén is érvényes.
- **BlazorApp:** a saját konfigurációbetöltési sorrendje és User Secrets-szabályai. Innen származik az OIDC authority, a kliensazonosító és klienshitelesítés, a callback, a downstream API címe és a szükséges scope-ok. A callback teljes HTTPS-címét a meglévő launch profile vagy az IIS Express SSL-portja adja.

A teszt ellenőrzi, hogy a futó WebAPI alapértelmezett provider-/modellaliasai egyeznek-e a forráskönyvtár aktuális beállításaival. Az API-kulcsot és a providerbeállításokat ne másold át a tesztprojektbe.

A korábbi `RGF_DEMO_API_URL`, `RGF_DEMO_ACCESS_TOKEN`, `RGF_DEMO_AI_MODEL` és `RGF_DEMO_DIAGNOSTICS_DIRECTORY` felülírásokat a teszt már nem használja.

## Gyakori hibák

| Jelenség | Ellenőrzés |
| --- | --- |
| Az élő esetek kimaradnak | A tesztfolyamatban legyen `RGF_DEMO_LIVE_LLM=1`. |
| A Duende elutasítja a bejelentkezést | Ellenőrizd, hogy a lokális Duende környezetben működik-e az `Admin` / `rgfdemo` nyilvános demóbelépés. A teszt nem olvas belépési környezeti változókat. |
| Nem érhető el a Duende vagy a WebAPI | Indítsd el a megfelelő szolgáltatást; adatbázishiba esetén előbb az adatbázis elérését javítsd. |
| Provider-/modellbeállítás eltérése | Ellenőrizd a WebAPI konfigurációját és User Secrets-ét, majd indítsd újra a backendet. |
| Hiányzó diagnosztika | Ellenőrizd az effektív `Recrovit:AI:Diagnostics` értékeket, az NLog `IncludeScopes` és Trace szabályokat, valamint a helyi naplófájlt. |
| Hiányzó Entity-/Property-nevek | Vizsgáld meg a helyi trace-t és a visszaadott üzenetet; ez valódi válaszminőségi hiba is lehet. Ne gyengítsd az ellenőrzést puszta HTTP-sikerre. |


## Beépített diagnosztika és NLog

A kizárólag helyben, explicit engedélyezett hibavizsgálat konfigurációja:

```json
{
  "Recrovit": {
    "AI": {
      "Diagnostics": {
        "Normalized": { "Enabled": true, "IncludeContent": true },
        "StructuredOutput": { "IncludeContent": true },
        "RawHttp": { "Enabled": false }
      }
    }
  }
}
```

A meglévő konfigurációs forrásokat használd. A Development indításban a providerhez tartozó User Secrets a környezeti változók után töltődik be, ezért felülírhatja ezeket. Ellenőrizd az **effektív** értékeket is; azonosítót és betöltési sorrendet ne változtass. A normál alkalmazásindításban mindhárom tartalomrögzítési kapcsoló kikapcsolt. Vizsgálat után töröld a helyi felülírásokat, vagy állítsd az `Enabled` / `IncludeContent` értékeket `false`-ra, majd indítsd újra a WebAPI-t. A naplók ettől nem törlődnek; a helyi, teljes tartalmú fájlokat szükség szerint külön töröld.

A napló helye mind Visual Studio, mind `dotnet run` esetén:

```text
<WebAPI Debug TargetDir>\logs\ai\events.jsonl
```

A WebAPI NLog `${basedir}` értéke az alkalmazás kimeneti könyvtára. A teszt `dotnet msbuild RGFDemo.WebAPI.csproj -property:Configuration=Debug -getProperty:TargetDir` projektértékeléssel oldja fel ugyanazt: ez nem indít buildet vagy restore-t. A szokásos hely `src/RGFDemo.WebAPI/bin/Debug/net10.0/logs/ai/events.jsonl`, de a teszt nem égeti be a frameworköt vagy ezt a kimeneti útvonalat. Visual Studio és `dotnet run` ugyanazt a Debug kimenetet használja, munkakönyvtártól függetlenül. Egyedi buildtulajdonságok esetén a projektértékeléshez is ugyanazokat a projektbeállításokat használd.

A fájl a webgyökéren kívül, a Git által kizárt buildkimenetben van, és nincs HTTP-endpointja. A fájlcél `autoFlush=true` mellett ír. A fájlt futó teszt közben ne csonkítsd vagy rotáld; a buildkimenet törlése a diagnosztikát is törli. A teszt és a WebAPI ugyanahhoz a lokális Debug kimenethez férjen hozzá.

A Demo `nlog.config` külön `JsonLayout` célba irányítja a normalizált, RAW, WorkflowRuntime és MafWorkflowAIInvoker kategóriákat, Trace szinten, lezáró szabályokkal. A `UseNLog` engedélyezi az `IncludeScopes` beállítást. A meglévő conversation-, workflow-, agent- és invocation-azonosítók külön JSON-mezők, ahol elérhetők; a TraceId hiányozhat. A promptok és tooleredmények nem kerülnek a `basic.log` fájlba vagy a konzolra. A napló nem rögzít HTTP-headereket, bearer/API-kulcsokat vagy conversation tokeneket.

A teszt minden POST előtt megjegyzi a fájl végét, majd csak az új sorokat és a válasz tényleges ConversationId/WorkflowRunId értékeit vizsgálja. A fordulók ezért elkülönülnek. A fájl rövid írási késésére legfeljebb 15 másodpercet vár; ez nem indít új LLM-kérést. A provider válasza `TrafficContent` esemény, a feldolgozott agentkimenet `StructuredContent` esemény: nincs régi `stage/data` vagy baseline contract.

A toolhívásokat a provider-válasz FunctionCall tartalma és a következő kérés azonos CallId értékű FunctionResult tartalma kapcsolja össze. A referencia a ténylegesen visszaadott jogosultságszűrt metadata. Teljes Entity-listához szűretlen keresés és összefüggő, nulláról induló laplánc kell, a TotalCount és NextOffset ellenőrzésével. A második forduló használhatja a már modellhez eljutott metadata-előzményt; új toolhívás nem kötelező.

A smoke előbb a funkcionális választ, majd külön diagnosztikai állításokkal a normalizált modellválasztást, tokenhasználatot és chat agentet ellenőrzi. A provider aliasát a védett futó konfigurációs katalógus igazolja. Ez nem bizonyítja a fizikai HTTP-kérés modellmezőjét vagy a HTTP-attemptök számát. Ehhez külön RAW-vizsgálat szükséges; RAW nem előfeltétele az általános smoke-nak.

A „Diagnosztikai hiba” a szükséges naplóesemény hiányát jelöli; a „Válaszminőségi hiba” a tényleges felhasználói válasz hiányosságát. A teljes katalógus el nem olvasása külön hibát ad. Egyik sem javítható a teljességi ellenőrzések gyengítésével. Az `Output` / `UserMessage` contract változatlan; a felsorolási hiba továbbra is vizsgálandó. Az új infrastruktúra futási eredményeit a [külön ellenőrzési jelentés](diagnostics-verification.md) tartalmazza; a [korábbi jelentés](verification.md) történeti bizonyíték marad.
