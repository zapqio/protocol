# Protokół Zapqio Runner — v1 (wersja robocza)

> **Uwaga o tłumaczeniu.** To jest polskie tłumaczenie [`PROTOCOL.md`](./PROTOCOL.md).
> Wersją **normatywną** (rozstrzygającą) pozostaje oryginał angielski — w razie jakiejkolwiek
> rozbieżności między tym dokumentem a `PROTOCOL.md` obowiązuje `PROTOCOL.md`.
> Nazwy pól, nagłówków HTTP, typów wiadomości i wartości wyliczeń **nie są tłumaczone** — na łączu
> występują dokładnie w formie angielskiej.

To jest **źródło prawdy** dla protokołu komunikacji między serwerem **Web** Zapqio a **Runnerem**.
Każdy runner — w dowolnym języku — który jest zgodny z tym dokumentem, może połączyć się z Web.

Ta wersja ma charakter *opisowy*: dokumentuje zachowanie referencyjnej implementacji .NET wg stanu na
2026-06-12. Referencyjne wiązanie (binding) znajduje się w `Zapqio.Protocol`; §11 mapuje każdą regułę
z tego dokumentu na kod, który ją realizuje, dzięki czemu specyfikację można ponownie zweryfikować.

Słowa kluczowe MUSI, NIE WOLNO, POWINIEN oraz MOŻE są używane w rozumieniu RFC 2119 i odpowiadają
angielskim MUST, MUST NOT, SHOULD i MAY.

---

## 1. Przegląd

**Runner** jest **klientem** WebSocket. **Web** jest **serwerem** WebSocket. Runner nawiązuje
połączenie, uwierzytelnia się nagłówkami HTTP, ogłasza metody, które potrafi wykonać, a następnie
odbiera zadania, wykonuje je, strumieniuje logi i zwraca wyniki. Każda wiadomość aplikacyjna to
tekstowa ramka JSON o wspólnej [kopercie](#4-koperta).

Web jest **agnostyczny** wobec tego, jak runner wykonuje zadanie ani w jakim języku jest napisany.
Kieruje zadania według pary `(runner, nazwa metody)` i traktuje wejście oraz wyjście każdego zadania
jako **nieprzezroczysty ciąg znaków**. Runnery mogą być więc niejednorodne: jeden pipeline może mieć
kroki wykonywane przez różne runnery napisane w różnych językach.

Kompletna implementacja runnera to trzy warstwy, ale **tylko pierwsza przekracza granicę języka**:

1. **Protokół** — ten dokument. (Do zaimplementowania ponownie w Twoim języku.)
2. **Host** — cykl życia połączenia, pętla zadań, kolejka logów, kierowanie po nazwie metody.
3. **Model modułów** — sposób, w jaki *Twój* język definiuje i wykonuje metody. To projektujesz sam
   (np. dekoratory + generator JSON Schema). Model .NET oparty na `IRunnerMethod` i ładowaniu
   assembly **nie jest przenośny**; moduły są specyficzne dla języka.


| Nadawca | Odbiorca | Komunikat / Akcja | Szczegóły i zachowanie serwera |
|---|---|---|---|
| **Runner** | **Web** | Nawiązanie połączenia (`GET /ws-runner`) | Przesyła nagłówki: `X-Zapqio-Token`, `X-Zapqio-Name`. Zwraca kod `101` (lub błędy `401`/`400`). |
| **Runner** | **Web** | `Info` (metody + nazwa) | Serwer rejestruje u siebie przesłane metody. |
| **Web** | **Runner** | `Job` (przydział) | Serwer aktywnie wypycha nowe zadanie do wykonania. |
| **Runner** | **Web** | `Log ... Log ...` | Strumieniowanie logów na żywo w trakcie wykonywania zadania. |
| **Runner** | **Web** | `JobReturn` (OK/ERROR + wynik) | Serwer zapisuje wynik i podaje go do kolejnego kroku. |
| **Runner** | **Web** | `Job` (odpytanie, `data=null`) | Klient zgłasza gotowość komunikatem „daj mi więcej pracy”. |


---

## 2. Transport i ramkowanie

- **WebSocket** (RFC 6455). Punkt końcowy: `GET {baseUrl}/ws-runner` ze standardowym upgrade, gdzie
  `{baseUrl}` to origin serwera Web (np. `wss://zapqio.example.com`).
- Wiadomości aplikacyjne to ramki **tekstowe**, **UTF-8**, każda będąca pojedynczym obiektem JSON
  (kopertą). Implementacje MUSZĄ scalać ramki kontynuacyjne aż do FIN przed parsowaniem.
- Maksymalny rozmiar wiadomości po stronie serwera: **32 MiB** (33 554 432 bajty). Większa wiadomość
  powoduje zamknięcie połączenia ze statusem WS **1009** (Message Too Big).
- Brak kompresji i grupowania (batching) na poziomie aplikacji.

---

## 3. Uzgadnianie połączenia i uwierzytelnianie

W żądaniu upgrade runner MUSI wysłać dwa nagłówki:

| Nagłówek | Znaczenie |
| --- | --- |
| `X-Zapqio-Token` | Sekretny token runnera, jawnym tekstem. Web weryfikuje go wobec zapisanych skrótów Argon2. |
| `X-Zapqio-Name`  | Stabilna, samodzielnie nadana nazwa runnera (z konfiguracji/zmiennej środowiskowej). |
| `X-Zapqio-Protocol-Version` | **Główna** wersja protokołu, którą mówi runner (np. `1`). Na razie opcjonalny; brak nagłówka jest traktowany jak `1`. |

Zachowanie serwera:

- Żądanie do `/ws-runner`, które **nie** jest upgrade WebSocket → HTTP **400**.
- Brakujący lub pusty którykolwiek z nagłówków → HTTP **401**.
- Token niepasujący do **żadnego** runnera → HTTP **401**.
- **Powiązanie nazwy:** przy pierwszym połączeniu Web wiąże `X-Zapqio-Name` z rekordem runnera. Przy
  kolejnych połączeniach nazwa MUSI być równa nazwie powiązanej, w przeciwnym razie → HTTP **401**.
- Nieobsługiwana wersja protokołu → HTTP **426 Upgrade Required** (odpowiedź niesie
  `X-Zapqio-Protocol-Version: <wersja serwera>`); wersja niebędąca liczbą całkowitą → HTTP **400**.
- W pozostałych przypadkach → **101 Switching Protocols**.

Uwagi:

- **Token jest tożsamością i sekretem**; **nazwa jest stabilną etykietą**. Wybierz nazwę raz i
  utrzymuj ją niezmienną. (Runner referencyjny odczytuje `ZAPQIO_NAME`, a jeśli nic nie
  skonfigurowano, generuje UUID i zapisuje go trwale do pliku `##Name`.)
- **Wersja protokołu** jest negocjowana nagłówkiem `X-Zapqio-Protocol-Version` (całkowita wersja
  główna). **Brak** nagłówka → przyjmuje się `1` (poziom bazowy sprzed wersjonowania); wartość
  **niecałkowita** → HTTP 400; wartość, której serwer **nie** obsługuje → **426 Upgrade Required**,
  z wersją serwera odesłaną w nagłówku odpowiedzi `X-Zapqio-Protocol-Version`. Wersja główna jest
  podnoszona wyłącznie przy zmianie łamiącej zgodność (§9).

---

## 4. Koperta

Każda wiadomość WebSocket to dokładnie taki obiekt:

```json
{ "type": "Job", "data": "…" }
```

- **`type`** *(string, wymagane)* — typ wiadomości: jeden z `Info`, `Job`, `JobReturn`, `Log`.
  Dokładna wielkość liter w §6.
- **`data`** *(string lub null, wymagane)* — ładunek dla danego typu, **zakodowany jako JSON w
  postaci ciągu znaków**. Obiekt ładunku jest serializowany do JSON, a ten *tekst* JSON trafia do
  `data` jako wartość tekstowa. `null` wyłącznie dla [odpytania Job](#52-job).

> **⚠ PUŁAPKA — podwójne kodowanie.** `data` jest **ciągiem znaków**, a nie zagnieżdżonym obiektem.
> Aby **odczytać** wiadomość: sparsuj kopertę, a następnie sparsuj `data` *ponownie* jako JSON. Aby
> **zapisać**: zserializuj ładunek do ciągu znaków i przypisz go do `data`. Rozpisane bajty w §8.

Wszystkie nazwy właściwości obiektów są w **camelCase** (`type`, `data`, `id`, `name`, `jobId`,
`level`, …).

---

## 5. Wiadomości i przepływ

Legenda kierunków: **R→W** runner→web, **W→R** web→runner.

### 5.1 Info (R→W)

Wysyłane **raz**, zaraz po pierwszym udanym połączeniu. Ogłasza nazwę runnera oraz metody, które
udostępnia. Ładunek — `MessageInfo`:

```json
{
  "name": "build-agent-01",
  "methods": [
    { "name": "resize-image", "in": "{\"type\":\"object\", … }", "out": "{\"type\":\"object\", … }" }
  ]
}
```

- `name` — nazwa runnera (ta sama wartość co `X-Zapqio-Name`).
- `methods` — tablica obiektów `MessageMethod`:
  - `name` — nazwa metody; zadania są do niej kierowane po tej nazwie.
  - `in`  — **JSON Schema** opisujący **wejście** metody, przenoszony *jako ciąg znaków*, albo `null`.
  - `out` — **JSON Schema** opisujący **wyjście** metody, przenoszony *jako ciąg znaków*, albo `null`.

Web zapisuje listę metod; interfejs użytkownika używa `in`/`out` do renderowania i walidacji wejścia
oraz wyjścia zadań w pipeline. Runner referencyjny wysyła `Info` raz na proces; **wyślij `Info`
ponownie, jeśli zmieni się zestaw metod** (np. po ponownym połączeniu z innym zestawem modułów).

### 5.2 Job

`Job` jest **przeciążony kierunkiem**:

- **Odpytanie (R→W):** koperta `{ "type": "Job", "data": null }`. Oznacza *„jestem wolny, przyślij mi
  pracę”*. Jeśli Web nie ma nic do przydzielenia, nie odsyła nic.
- **Przydział (W→R):** ładunek — `MessageJob`:

  ```json
  { "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890", "name": "resize-image", "data": "{\"width\":800}" }
  ```

  - `id`   — unikalny identyfikator zadania; odeślij go w `Log` i `JobReturn`.
  - `name` — która metoda ma zostać uruchomiona (pasuje do `MessageMethod.name`).
  - `data` — **wejście** zadania: samo w sobie ciąg znaków JSON (zgodny ze schematem `in` metody),
    możliwe że pusty.

> **⚠ PUŁAPKA — potrójne zagnieżdżenie.** `envelope.data` jest ciągiem znaków zawierającym JSON
> obiektu `MessageJob`, a `MessageJob.data` jest *znowu* ciągiem znaków zawierającym JSON wejścia
> zadania. To dwa poziomy kodowania tekstowego ponad samą ramką.

**Rozruch i kadencja.** Po `Info` runner blokuje się na odbiorze; **pierwsze** zadanie przychodzi z
dyspozytora działającego w tle po stronie Web (co ok. 10 s). Runner wykonuje **jedno zadanie naraz**
i po zakończeniu każdego zadania wysyła **odpytanie Job**, aby pobrać kolejne. Przydział, który
nadejdzie w trakcie wykonywania zadania, MOŻE zostać zignorowany przez runnera (runner referencyjny
go odrzuca).

### 5.3 Log (R→W)

Strumieniowane w trakcie wykonywania zadania. Ładunek — `MessageLog`:

```json
{
  "jobId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "level": "Info",
  "message": "Run Job: 2026-06-12T14:30:00",
  "date": "2026-06-12T14:30:00.123+00:00"
}
```

- `jobId`   — zadanie, do którego należy dany wpis logu.
- `level`   — `Info` albo `Error` (§6).
- `message` — treść logu.
- `date`    — znacznik czasu ISO-8601 z przesunięciem strefy czasowej (§7).

**Pierwszy** `Log` dla zadania przełącza je po stronie serwera ze stanu *Waiting* na *Executing*.
Runner referencyjny przechwytuje `stdout` metody→`Info` oraz `stderr`→`Error`, a także wiersz
startowy i ewentualny wyjątek; wpisy są opróżniane z kolejki na timerze co ok. 2 s, **jedna
wiadomość WS na wpis**.

### 5.4 JobReturn (R→W)

Wysyłane **raz**, gdy zadanie się zakończy. Ładunek — `MessageJobReturn`:

```json
{ "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890", "status": "OK", "data": "{\"url\":\"…\"}" }
```

- `id`     — identyfikator zadania.
- `status` — `OK` albo `ERROR` (§6).
- `data`   — przy `OK`: **wyjście** zadania (ciąg znaków JSON zgodny z `out`), które Web podaje jako
  **wejście kolejnego kroku pipeline**. Przy `ERROR`: `null`.

### 5.5 Typowa wymiana (jedno zadanie)

```
W→R  Job        { id: J, name: "resize-image", data: "{…wejście…}" }
R→W  Log        { jobId: J, level: Info,  message: "Run Job: …" }
R→W  Log        { jobId: J, level: Info,  message: "…wyjście metody…" }
R→W  JobReturn  { id: J, status: OK, data: "{…wyjście…}" }
R→W  Job        null                              # odpytanie o kolejne zadanie
```

---

## 6. Wyliczenia (dokładne wartości na łączu)

Wyliczenia są serializowane jako ich **dokładne nazwy** — implementacja referencyjna używa konwertera
wyliczeń na ciągi znaków **bez polityki nazewnictwa**, więc wartości są w **PascalCase / wielkimi
literami, a NIE w camelCase**. Producenci MUSZĄ emitować dokładnie te ciągi:

| Pole | Dozwolone wartości |
| --- | --- |
| `type` wiadomości  | `Info`, `Job`, `JobReturn`, `Log` |
| `level` w Log      | `Info`, `Error` |
| `status` w JobReturn | `OK`, `ERROR` |

Referencyjny *deserializator* .NET akceptuje przy odczycie także inną wielkość liter oraz wartości
całkowite, ale zgodny producent MUSI emitować dokładnie powyższe ciągi i NIE WOLNO mu emitować
liczb całkowitych.

---

## 7. Formaty skalarne

| Typ logiczny | Format na łączu | Przykład |
| --- | --- | --- |
| uuid (`id`, `jobId`) | kanoniczny UUID z myślnikami, małymi literami | `"a1b2c3d4-e5f6-7890-abcd-ef1234567890"` |
| znacznik czasu (`date`) | ISO-8601 z przesunięciem strefy czasowej; ułamki sekund opcjonalne | `"2026-06-12T14:30:00.123+00:00"` |
| JSON Schema (`in`, `out`) | dokument JSON Schema przenoszony **jako ciąg znaków** | `"{\"type\":\"object\", … }"` |
| wejście/wyjście zadania (`data` w `MessageJob`/`MessageJobReturn`) | nieprzezroczysty JSON przenoszony **jako ciąg znaków**; kształt definiowany per metoda przez `in`/`out`, a nie przez ten protokół | `"{\"width\":800}"` |

---

## 8. Przykład krok po kroku — bajty na łączu

**Przydział Job** dla metody `resize-image` z wejściem `{"width":800,"height":600}`. Dokładna ramka
tekstowa, którą wysyła Web:

```
{"type":"Job","data":"{\"id\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\",\"name\":\"resize-image\",\"data\":\"{\\\"width\\\":800,\\\"height\\\":600}\"}"}
```

Dekodowanie, poziom po poziomie:

1. **Ramka → koperta:** `{ "type": "Job", "data": "{\"id\":\"a1b2…\",\"name\":\"resize-image\",\"data\":\"{\\\"width\\\":800,…}\"}" }`
2. **Parsowanie `data` → `MessageJob`:** `{ "id": "a1b2…", "name": "resize-image", "data": "{\"width\":800,\"height\":600}" }`
3. **Parsowanie `MessageJob.data` → wejście:** `{ "width": 800, "height": 600 }`

Każdy fixture w katalogu [`fixtures/`](./fixtures/) to jedna taka dokładna ramka.

---

## 9. Wersjonowanie i zgodność

- To jest **v1**, opisowa wobec bieżącej implementacji referencyjnej.
- **Negocjacja wersji** odbywa się podczas uzgadniania połączenia przez nagłówek
  `X-Zapqio-Protocol-Version` (§3): runner wysyła swoją wersję główną, a serwer akceptuje wyłącznie
  wersje, które obsługuje, odpowiadając w przeciwnym razie **426 Upgrade Required**. Brak nagłówka
  jest traktowany jak `1` dla zgodności wstecznej z runnerami sprzed wersjonowania.
- Zasady zgodności dla przyszłych rewizji: dodanie pola **opcjonalnego** jest zgodne wstecz;
  usunięcie lub zmiana nazwy pola, albo zmiana ciągu wyliczenia, **łamie zgodność** i wymaga
  podniesienia wersji.

---

## 10. Zgodność ze specyfikacją

Implementacja jest zgodna, jeżeli potrafi zarówno **wyprodukować**, jak i **skonsumować** każdy
fixture z katalogu [`fixtures/`](./fixtures/) w taki sposób, że po pełnym zdekodowaniu (ramka →
koperta → ładunek → zagnieżdżone wejście/wyjście zadania) zawartość logiczna jest równa zawartości
udokumentowanej dla danego fixture'a.

Porównanie jest **semantyczne** (sparsowane struktury są głęboko równe), a **nie bajt w bajt**:
nieznaczące białe znaki i kolejność kluczy w obiektach nie mają znaczenia. Producenci POWINNI mimo to
emitować wielkość liter w nazwach pól oraz ciągi wyliczeń dokładnie tak, jak określono, ponieważ nie
każdy konsument jest pobłażliwy.

---

## 11. Mapa implementacji referencyjnej

Ten dokument, wraz z [`schemas.json`](./schemas.json) i [`fixtures/`](./fixtures/), jest *pomyślany*
jako źródło prawdy: kod .NET w tym repozytorium jest **jedną** z implementacji, a nie definicją
protokołu.

Miej świadomość obecnej luki — żaden automatyczny zestaw testów nie sprawdza jeszcze kodu .NET wobec
fixture'ów, więc w praktyce kod może odejść od tego dokumentu i nic nie zasygnalizuje błędu. Dopóki
taki zestaw testów nie powstanie, każdą rozbieżność między kodem a specyfikacją traktuj jako błąd
wart zgłoszenia, a nie jako stan uzgodniony.

Runner referencyjny jest **klientem** WebSocket. Jego układ, dla czytelników, którzy chcą zobaczyć
daną regułę w działającym kodzie:

| Zagadnienie | Plik |
| --- | --- |
| Koperta + opcje JSON (camelCase, wyliczenia jako ciągi) | `Zapqio.Protocol/Message.cs`, `Zapqio.Protocol/JsonDefaults.cs` |
| Kształty ładunków | `Zapqio.Protocol/Message{Info,Method,Job,JobReturn,Log}.cs` |
| Definicje wyliczeń | `Zapqio.Protocol/Enums/Message{Type,LogLevel,ResponseStatus}.cs` |
| Stała negocjowanej wersji | `Zapqio.Protocol/ProtocolVersion.cs` |
| Uzgadnianie połączenia i wysyłka (klient) | `Zapqio.Runner/WSClient.cs` |
| Pętla runnera (Info, odpytanie, przydział) | `Zapqio.Runner/Background/RequestBindBackground.cs` |
| Kolejka logów i kadencja opróżniania | `Zapqio.Runner/Background/SendLogsBackground.cs`, `Zapqio.Runner/LogQueue.cs`, `Zapqio.Runner/ScopedConsole.cs` |

Strona **serwera** (Web) nie jest częścią tego repozytorium. Wszystko, czego runner potrzebuje do
współpracy z nim, jest określone tutaj: uzgadnianie połączenia i jego kody odrzucenia (§3), koperta
(§4), zestaw wiadomości i ich kierunki (§5) oraz zasady negocjacji wersji (§9). Nie wolno polegać na
żadnym zachowaniu serwera wykraczającym poza ten dokument.
