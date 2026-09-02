# ScaleTrigger — Preostale stavke za implementaciju

> **Status (2026-09-02):** Faze 0, 1 i 2 su implementirane i verificirane (`dotnet test` — 33/33 prolazi, uključujući MSSQL Testcontainers protiv stvarnog Docker containera). Faza 3 i Faza 4 su namjerno preskočene — obje su u ovom dokumentu već označene kao svjesni kompromisi/nice-to-have niskog prioriteta, ne kao stavke koje nedostaju. Detalji po stavci ostaju ispod radi traga, s ✅ dodanim uz svaku dovršenu.

Ovaj dokument nastaje nakon što je prethodni roadmap (`ScaleTrigger_Roadmap.md`) gotovo u cijelosti implementiran u repozitoriju [dopiskur/scaleTrigger](https://github.com/dopiskur/scaleTrigger). Sadržaj je provjeren svježim kloniranjem repozitorija i čitanjem izvornog koda — ne pretpostavljen.

## Što je već implementirano (potvrđeno u kodu)

Radi konteksta, kratak pregled onoga što **više nije potrebno raditi**, jer je već prisutno u repozitoriju:

- Rampana alokacija memorije (`SimulateMemoryLoadAsync`, 1 MB chunkovi + delay) i globalni budžet `LoadSafety:MaxConcurrentMemoryBytes`
- `Task.Run` uklonjen iz `VoteAdd`, s komentarom koji objašnjava zašto
- `/health/live` i `/health/ready` endpointi
- Request timeout (300s, kalibriran iznad `MaxAllowedValues` raspona)
- Startup upozorenje ako `AdminUser:Password` još ima default `admin` vrijednost
- Serilog (JSON) + `CorrelationIdMiddleware` (`X-Correlation-Id`)
- Polly retry (`DbRetryPolicy`), namjerno ograničen na read-only pozive, ne na `VoteAddAsync`
- OpenTelemetry + Prometheus (`/metrics`), uključujući `ActiveVoteTracker` gauge za broj aktivnih `VoteAdd` poziva u letu
- `wwwroot/index.html` rastavljen na `css/dashboard.css` + 7 JS modula (`auth.js`, `benchmark.js`, `latency.js`, `loadconfig.js`, `main.js`, `report.js`, `shared.js`)
- Load profile presets (Light/Medium/Heavy) u `loadconfig.js`
- Export/Import `LoadConfig` kao JSON
- ~~"Currently: ..." summary red na dashboardu (trenutni efektivni load)~~ — uklonjeno 2026-09-02 na korisnički zahtjev (dashboard tekst procijenjen kao suvišan)
- `INSECURE_TLS` env varijabla u `scaleTriggerLoad_k6.js` i `scaleTriggerLoad_locust.py`
- Dokumentacijske korekcije u `README.md` i `deploy/azure-demo-resources/README.md` (probe-vote napomena, TLS napomena, dijeljena lozinka napomena)
- **Testcontainers-backed integracijski testovi za MySQL/PostgreSQL** (commit `cbd91ba`) — `ProviderIntegrationTestsBase<TFactory>` dijeljena baza, `MySqlScaleTriggerApplicationFactory`/`PostgreSqlScaleTriggerApplicationFactory`, jedan container po test klasi. Ovo je usput otkrilo i popravilo pravi produkcijski bug: `PostgreSqlRepository.EnsureSchemaAsync`/`LoadConfigEnsureSeededAsync` su bacali `InvalidCastException` na `SELECT to_regclass(...)` protiv Npgsql 10.x (regclass OID tip se više ne mapira automatski) — `DatabaseProvider: PostgreSql` protiv svježe baze nikad nije radio dok ovaj test nije napisan. Popravljeno s `::text` castom na oba mjesta. 28/28 testova prolazi (18 SQLite + 5 MySQL + 5 PostgreSQL).

`Immutable/ConcurrentDictionary` za `LoadConfigCache` namjerno **nije** promijenjeno — postojeći `volatile Dictionary` + atomic swap pattern je već ispravan lock-free pristup, promjena ne bi ništa popravila.

---

## Preostalo za implementaciju

*Numeracija je isključivo unutar svake faze radi referenciranja u razgovoru.*

### Faza 0 — MSSQL integracijsko pokrivanje (najviši prioritet) ✅

1. ✅ **`MsSqlScaleTriggerApplicationFactory` po istom obrascu kao `MySqlScaleTriggerApplicationFactory`/`PostgreSqlScaleTriggerApplicationFactory`.** MSSQL je jedini od četiri providera koji integracijski testovi iz `cbd91ba` ne pokrivaju — a upravo je PostgreSQL slučaj u tom istom commitu dokazao da ovakav bug (kod koji tiho puca protiv svake žive instance, neovisno o statičkoj analizi ili code reviewu) ostaje neotkriven dok se ne napiše pravi integracijski test protiv stvarnog containera. `MsSqlRepository` je zasad jedini repozitorij bez ikakvog testa protiv žive baze.

   Paket: `Testcontainers.MsSql` verzija `4.14.0` postoji i dio je iste obitelji kao već korišteni `Testcontainers.MySql`/`Testcontainers.PostgreSql` paketi (isti maintainer, ista release verzija).

   Bitna razlika naspram MySQL/PostgreSQL: `MsSqlBuilder` je community-maintained modul s užim API-jem — nema `.WithDatabase()`/`.WithUsername()`, samo `.WithPassword()`/`.WithImage()`, a `GetConnectionString()` vraća konekciju na `master` pod `sa` loginom. Budući da `MsSqlRepository` u produkciji uvijek očekuje već postojeću imenovanu bazu (`appsettings.json.example`: `Database=ScaleTriggerDb`, nikad `CREATE DATABASE` u samom kodu), factory mora sam kreirati imenovanu bazu u `InitializeAsync` prije nego se app poveže:

   ```csharp
   public class MsSqlScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory, IAsyncLifetime
   {
       private const string DatabaseName = "ScaleTriggerTests";

       private readonly MsSqlContainer container = new MsSqlBuilder()
           .WithPassword("ScaleTrigger!Test1")
           .Build();

       private string connectionString = string.Empty;

       protected override string DatabaseProvider => "MsSql";
       protected override string ConnectionStringKey => "ConnectionStrings:MsSql";
       protected override string ConnectionStringValue => connectionString;

       public async Task InitializeAsync()
       {
           await container.StartAsync();

           string masterConnectionString = container.GetConnectionString();

           await using (var connection = new SqlConnection(masterConnectionString))
           {
               await connection.OpenAsync();
               await using var command = connection.CreateCommand();
               command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
               await command.ExecuteNonQueryAsync();
           }

           connectionString = new SqlConnectionStringBuilder(masterConnectionString)
           {
               InitialCatalog = DatabaseName
           }.ConnectionString;
       }

       async Task IAsyncLifetime.DisposeAsync() => await container.DisposeAsync();
   }
   ```

   Plus `MsSqlIntegrationTests : ProviderIntegrationTestsBase<MsSqlScaleTriggerApplicationFactory>` u `ProviderIntegrationTestsBase.cs` (identičan pattern kao `MySqlIntegrationTests`/`PostgreSqlIntegrationTests`), `<PackageReference Include="Testcontainers.MsSql" Version="4.14.0" />` u `.csproj`, i ažuriranje README odlomka koji trenutno tvrdi "MSSQL isn't covered by either layer".

   Napomene za onoga tko ovo implementira:
   - `RepoFactory`-jev `useManagedIdentity` ostaje `false` bez dodatne konfiguracije — čita `configuration["UseManagedIdentity"]`, koji ostaje `null`/neparsiran u testnom okruženju, isto kao i za bazni Sqlite factory.
   - MSSQL container ima sporiji cold start od MySQL/PostgreSQL — ako prvi test padne na timeout, provjeriti treba li eksplicitni `WaitStrategy` umjesto oslanjanja na builderov default.
   - Lozinka mora zadovoljiti MSSQL SA complexity pravila (min. 8 znakova, 3 od 4 klase znakova).
   - Ovo nije stvarno pokrenuto niti verificirano protiv Dockera u ovoj analizi (nema dostupan .NET SDK/Docker) — prije commita nužno pokrenuti `dotnet test` lokalno da se potvrdi da container stvarno startuje i da `CREATE DATABASE` korak radi.

   **Napomena:** implementirano i pokrenuto protiv stvarnog `mcr.microsoft.com/mssql/server:2022-latest` containera (Docker Desktop lokalno) — svih 5 testova prolazi (~350ms za prvi test, ~7s ukupno uz container start/stop). `MsSqlBuilder()` bez argumenata je od 4.14.0 obilježen kao obsolete (upozorava na ukidanje parameterless konstruktora) — korišten je `new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")` da build bude bez warninga.

### Faza 1 — Manji propusti u već pokrivenim područjima ✅

1. ✅ **`Reset` endpoint nema try/catch**, za razliku od `VoteAdd` koji ga sad ima. Ako baza padne usred `DropSchemaAsync`/`EnsureSchemaAsync`/`LoadConfigEnsureSeededAsync`, klijent dobiva generički 500 bez `DbErrorResponse` klasifikacije, umjesto dosljednog 503 s `DbFailureKind` kao ostatak API-ja. Popravak je izravna primjena istog patterna koji `VoteAdd` već koristi:

   ```csharp
   public async Task<ActionResult> Reset()
   {
       var repo = repoFactory.GetRepo();

       try
       {
           await repo.DropSchemaAsync();
           await repo.EnsureSchemaAsync();

           var defaults = LoadConfigDefaults.ReadFrom(configuration);
           await repo.LoadConfigEnsureSeededAsync(defaults);
           loadConfigCache.Set(await repo.LoadConfigGetAsync());

           repoFactory.GetCache().RemoveItem(ReportCacheKey);

           return Ok();
       }
       catch (Exception ex)
       {
           var failureKind = repo.ClassifyException(ex);
           logger.LogWarning(ex, "Reset failed (DbFailureKind={DbFailureKind}).", failureKind);
           return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(failureKind));
       }
   }
   ```

2. ✅ **SQLite `ClassifyException` i dalje koristi string-matching** (`sqliteEx.Message.Contains("no such table", ...)`), dok MSSQL/MySQL/PostgreSQL koriste stabilne numeričke kodove. Popravak: zamijeniti provjerom `SqliteException.SqliteErrorCode`/`SqliteExtendedErrorCode` (SQLITE_ERROR = 1, s extended code koji označava missing table) — stabilnije kroz verzije `Microsoft.Data.Sqlite` paketa.

   **Napomena:** implementirano bez `SqliteExtendedErrorCode` provjere — SQLite nema poseban extended kod za "missing table" (i dalje se svodi na generički SQLITE_ERROR = 1). Umjesto message-matchinga, uklonjena je provjera teksta poruke i ostavljena samo `sqliteEx.SqliteErrorCode == 1`: sve SQL naredbe u `SqliteRepository` su fiksni stringovi (nikad iz korisničkog inputa), pa je jedini realan način da baš one izazovu SQLITE_ERROR nedostajuća tablica/kolona — syntax greška u statičkom SQL-u bi pucala na svakom pozivu, ne samo nakon Reseta. Provjera samo numeričkog koda je time jednako precizna kao ranije, i stabilnija kroz verzije paketa nego točan tekst poruke.

3. ✅ **`NodeBenchmark.cachedHardwareInfo` i dalje nema zaključavanje.** Dva paralelna prva poziva na hladnom startu i dalje mogu oba pokrenuti detekciju hardvera (uključujući HTTP pozive metadata servisu). Bezopasno (idempotentno), ali vrijedi popraviti istim `Lazy<Task<T>>` patternom koji `DiskLoadDirectory` u `LoadSimulator.cs` već koristi za analogan problem:

   ```csharp
   private static readonly Lazy<Task<NodeHardwareInfo>> CachedHardwareInfo =
       new(DetectHardwareInfoAsync, LazyThreadSafetyMode.ExecutionAndPublication);

   public static Task<NodeHardwareInfo> GetHardwareInfoAsync() => CachedHardwareInfo.Value;
   ```

### Faza 2 — Load-test skripte ✅

1. ✅ **Probe-poziv za detekciju auth-a i dalje upisuje stvaran glas** u `scaleTriggerLoad.py`, `scaleTriggerLoad_k6.js` i `scaleTriggerLoad_locust.py` — svaki test run dodaje jedan extra "yes" glas i prolazi punu load-simulaciju samo radi provjere je li `Auth:Enabled`. Popravak zahtijeva mali backend dodatak:

   - Novi anonimni endpoint `GET /api/auth/status` koji vraća `{ "authRequired": true|false }` bez ikakvog sporednog efekta (čita `Auth:Enabled` iz konfiguracije, bez upisa u bazu)
   - Sve tri skripte prebaciti s `POST /api/vote/add?option=yes` probe poziva na taj novi endpoint

2. ✅ **Python skripta (`scaleTriggerLoad.py`) i dalje koristi privatni `asyncio.Semaphore._value`** (`# noqa: SLF001`) za provjeru drenaže in-flight zahtjeva. Popravak: zamijeniti javnim brojačem koji se ažurira eksplicitno oko svakog zahtjeva:

   ```python
   in_flight = 0
   in_flight_lock = asyncio.Lock()

   async def bounded_send(...):
       nonlocal in_flight
       async with in_flight_lock:
           in_flight += 1
       try:
           # ... postojeća logika slanja zahtjeva
       finally:
           async with in_flight_lock:
               in_flight -= 1

   # umjesto: while semaphore._value < max_in_flight_requests
   while in_flight < max_in_flight_requests:
       ...
   ```

   **Napomena:** implementirano bez `asyncio.Lock` — `in_flight += 1`/`-= 1` su pojedinačni izrazi bez `await` unutar sebe, pa u single-threaded asyncio event loopu ne mogu biti prekinuti usred izvršavanja; lock ovdje ne bi ništa dodatno osigurao, samo overhead.

### Faza 3 — Deploy / Bicep (namjerni kompromisi, nizak prioritet)

1. **Dijeljena `adminPassword` kroz tri sigurnosna konteksta** (VM/VMSS OS, Azure SQL, ScaleTrigger JWT) ostaje kako jest — dokumentirano u `README.md` kao svjestan kompromis za one-click demo, ne kao previd. Ako se ipak želi razdvojiti: dodati opcionalan `@secure() param appAdminPassword string = adminPassword` u `main.bicep`, proslijediti ga zasebno u `authPassword` unutar `single-vm.bicep`/`scale-set.bicep` umjesto `adminPassword`. Nizak prioritet — mijenja dizajn koji je namjerno jednostavan za demo scenarij.

2. **Nema automatske validacije lozinke** protiv zabranjenih znakova (`;`, `"`, backtick) prije deploya — samo opisano u `@description` komentaru. Popravak bi bio Bicep `@minLength`/regex provjera ili runtime provjera u `Run-ScalingScenarios.ps1` koja odbija lozinku s tim znakovima prije slanja. Nizak prioritet, isti razlog kao gore.

### Faza 4 — Nice-to-have (nepromijenjeno iz prethodnog roadmapa)

1. CLI alat — nizak prioritet, potvrđeno i dalje nedostaje
2. API verzioniranje (`/api/v1/...`) — nizak prioritet dok API nema vanjske potrošače
3. WebSocket umjesto pollinga — kozmetičko poboljšanje
4. Multi-tenant, Chaos mode — realno neisplativo s obzirom na veličinu ciljane publike (MCT zajednica)

---

## Napomena o `ThreadPriority.Highest`

`NodeBenchmark.cs` i dalje koristi `ThreadPriority.Highest` za CPU benchmark niti, bez promjene. Ovo nije bug — samo vrijedi dokumentacijska napomena (nije kod-promjena) da efekt te postavke u kontejneriziranom okruženju (cgroups/CFS limiti) može biti ograničen; ako još nije spomenuto u README-u, vrijedi dodati jednu rečenicu.
