# Fixture card: add a tiny calc module (self-test only — not a real task)

## Что будет сделано
1. Add `add(a, b)` to `work/calc.js`.
2. Add `sub(a, b)` to `work/calc.js`.
3. Add a test for both functions in `work/calc.test.js`.
4. Add usage examples for both functions to `work/README.md`.

## Чем закрывается
`work/calc.js` exports `add` and `sub`; `work/calc.test.js` has a passing test that
exercises both; `work/README.md` documents both with an example.

## Что НЕ входит
Multiplication or division support.
