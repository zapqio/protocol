# Fixture'y zgodności

Każdy plik `*.json` w tym katalogu to **jedna dokładna tekstowa ramka WebSocket** — dosłowne bajty,
które zgodna implementacja wysyła albo przyjmuje. Ponieważ pole `data` koperty jest ciągiem znaków
JSON (zob. [`../PROTOCOL.md` §4](../PROTOCOL.md#4-koperta)), kodowanie widać jako wyescape'owane
cudzysłowy wewnątrz `data` — tak ma być, dokładnie tak wyglądają w praktyce opisane pułapki.

Międzyjęzykowy test zgodności POWINIEN dla każdego fixture'a:

1. **Skonsumować:** sparsować ramkę, potem `data`, a potem (dla zadań i wyników) zagnieżdżone
   wejście/wyjście zadania, i sprawdzić, że w pełni zdekodowana treść równa się kolumnie
   **Znaczenie po zdekodowaniu** poniżej.
2. **Wyprodukować:** zbudować tę samą wiadomość logicznie i sprawdzić, że koduje się z powrotem do
   ramki **semantycznie równej** fixture'owi (głęboka równość po zdekodowaniu — białe znaki i
   kolejność kluczy nie mają znaczenia; zob.
   [`../PROTOCOL.md` §10](../PROTOCOL.md#10-zgodność-ze-specyfikacją)).

Wszystkie fixture'y należą do jednego spójnego przykładu: runner `build-agent-01`, metoda
`resize-image`, zadanie `a1b2c3d4-e5f6-7890-abcd-ef1234567890` wysłane jako próba
`7f3e9c21-4b8a-4d15-9e62-0c5a7b1d8f34`. Ten sam `attemptId` przewija się przez przydział,
potwierdzenie, oba logi i oba warianty wyniku — dokładnie tak, jak wymaga tego §5.2.

| Fixture | Kier. | Znaczenie po zdekodowaniu |
| --- | --- | --- |
| `info.json` | R→W | Runner ogłasza nazwę `build-agent-01`, jedną metodę `resize-image` ze schematem JSON wejścia (`width`, `height`) oraz wyjścia (`url`) i pojemność `maxConcurrency=4` (cztery zadania naraz). |
| `info-sequential.json` | R→W | To samo `Info` **bez** `maxConcurrency` — tak wysyła runner sprzed tego pola albo runner wykonujący jedno zadanie naraz. Konsument MUSI odczytać je jako pojemność `1`. |
| `job-poll.json` | R→W | Odpytanie: `type=Job`, `data=null` („przyślij mi pracę”). |
| `job-dispatch.json` | W→R | Przydział zadania `a1b2…` jako próba `7f3e…` → metoda `resize-image`, wejście `{ "width": 800, "height": 600 }`. |
| `job-accepted.json` | R→W | Potwierdzenie odbioru przydziału `a1b2…` / `7f3e…`. |
| `log-info.json` | R→W | Log poziomu Info dla zadania `a1b2…`, próba `7f3e…`: `"Run Job: 2026-06-12T14:30:00"`. |
| `log-error.json` | R→W | Log poziomu Error dla zadania `a1b2…`, próba `7f3e…`: `"Main exception: boom"`. |
| `job-return-ok.json` | R→W | Wynik zadania `a1b2…`, próba `7f3e…`: `OK`, wyjście `{ "url": "https://cdn.example.com/out/123.png" }`. |
| `job-return-error.json` | R→W | Wynik zadania `a1b2…`, próba `7f3e…`: `ERROR`, `data=null`. |

Dekodowanie `job-dispatch.json` krok po kroku jest rozpisane w
[`../PROTOCOL.md` §8](../PROTOCOL.md#8-przykład-krok-po-kroku--bajty-na-łączu).
