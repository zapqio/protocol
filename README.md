# Protokół Zapqio Runner

Ten katalog jest **niezależnym od języka źródłem prawdy** dla protokołu komunikacji między serwerem
**Web** Zapqio a **Runnerem**. Runner napisany w *dowolnym* języku może połączyć się z Web, o ile
jest z nim zgodny.

| Plik | Przeznaczenie |
| --- | --- |
| [`PROTOCOL.md`](./PROTOCOL.md) | Specyfikacja normatywna — **czytaj to najpierw**. |
| [`schemas.json`](./schemas.json) | JSON Schema (Draft 2020-12) dla koperty i wszystkich ładunków. |
| [`fixtures/`](./fixtures/) | Kanoniczne przykładowe wiadomości, używane jako międzyjęzykowe wektory zgodności. |

**Status: v4, kontrakt przygotowany do implementacji.** Dodaje obowiązkowy heartbeat i jego
potwierdzenie, gotowość sesji oraz uzgadnianie procesu i aktywnych prób po każdym połączeniu.
Web i oba runnery wymagają dostosowania oraz wspólnego odbioru przed wdrożeniem. Aktualizacja
tego repozytorium sama nie zmienia wersji działających aplikacji. Wersja protokołu jest
uzgadniana przy nawiązywaniu połączenia (§3); zob.
[`PROTOCOL.md` §9](./PROTOCOL.md#9-wersjonowanie-i-zgodność).

Protokół to zwykły JSON po WebSockecie, więc da się go zaimplementować w dowolnym języku. Zwróć
uwagę, że *runner* to więcej niż protokół: to także **host** (pętla połączenia, odpytywania i logów)
oraz **model modułów specyficzny dla języka** (sposób, w jaki definiuje się i wykonuje metody).
Granicę języka przekracza wyłącznie protokół — zob. [`PROTOCOL.md` §1](./PROTOCOL.md#1-przegląd).
