# Visor redesign — финальный отчёт (§20)

Дата: 2026-10-13. Сборка/тесты: `dotnet build Visor.sln -c Release` (0 ошибок), `dotnet test` — **279 passed**, 3 skipped (symlink PathSecurity).

## 1. Визуальное направление

Nav-rail shell (Home / Games / Presets / System / Network / Tools), карточки поверх существующих theme brushes, без нового «AI purple» стека. Язык EN/RU через `Loc`. Акцент — существующий cyan (`AccentBrush`), статусы Success/Warning.

## 2. Переработанные экраны

| Экран | Что изменилось |
|-------|----------------|
| Shell / MainWindow | `ShellTabControl`, nav-rail, Ctrl+1…6, PerMonitorV2 shell fit |
| Home | Session / Display / Library / Status, Suggestions, Recent games/activity |
| Games | Единый список + вкладки Display/Color/Session/Companions |
| Presets | Sticky editor, hotkeys без NumPad-зависимости по умолчанию |
| System | Overview, processes, SafeAction Game Mode + High performance |
| Network | Adapter, ICMP latency/loss/jitter, DNS resolve (read-only) |
| Tools | Defaults / History / Log |
| Settings / Theme / Wizard / Palette | Высоты под ~768@125%, `EnsureInsideWorkArea` |

## 3. Расположение кнопок

- Pause / Reset — низ сайдбара; Settings — иконка в сайдбаре.
- Games: sticky Apply / Launch; ⋯ — Duplicate/Export/Import/Restore.
- System SafeAction: Apply + Undo рядом с Game Mode / Power.
- Network: Start/Stop ICMP, Resolve/Stop DNS.
- Home Suggestions: Apply (SafeAction или navigate) + Dismiss.

## 4. Анимации

`UiMotion.SoftContentIn` при смене вкладки; `PopIn` на диалогах; без агрессивных glow/flash. Debounce shell-fit на DPI/LocationChanged (~280 ms).

## 5. Компоновка и скролл

Вложенные `MaxHeight` списки (`CompanionList`, `ProcessList`) — `NestedScroll.ForwardWheelAtEdge`. Страницы в `ScrollViewer`; Settings/Theme/Wizard — контент в скролле, футер с Save снаружи.

## 6. Окна и диалоги

`OverlayWin32.EnsureInsideWorkArea` на open; `UiStateStore.ClampToWorkAreas` для restore; maximize через `WM_GETMINMAXINFO` в work area. `ShellFit` — mins/default для 100/125/150%. Диалоги Owner + CenterOwner / Esc.

## 7. Добавленные функции (фазы 1–8)

1. Shell nav + Ctrl+1…6  
2. Home dashboard + Ctrl+K palette  
3. Activity history (`history.json`)  
4. System overview + process sample  
5. SafeAction Game Mode / High performance (+ Undo)  
6. Network diagnostics (ICMP + DNS timing)  
7. Recommendation engine v0 на Home  
8. ShellFit + DPI/768 polish + wheel fix  

## 8. ≥15 идей (приоритет)

| # | Идея | P |
|---|------|---|
| 1 | Авто-применение suggestions по расписанию (opt-in) | P2 |
| 2 | Wi-Fi signal / SSID (без паролей) на Network | P2 |
| 3 | Continuous ping graph | P3 |
| 4 | DNS change via SafeAction (сейчас запрещено) | P3 |
| 5 | Recommendation dismiss expiry | P2 |
| 6 | Per-game Game Mode force | P2 |
| 7 | Multi-gateway probe | P3 |
| 8 | Portable settings export pack | P2 |
| 9 | Accessibility high-contrast theme preset | P2 |
| 10 | Command palette recent commands | P3 |
| 11 | Overlay DPI unit tests beyond ShellFit | P3 |
| 12 | Crash dump upload opt-in | P3 |
| 13 | Profile conflict resolver UI | P2 |
| 14 | ARM64 publish smoke | P3 |
| 15 | In-app QA mode that skips display writes | P1 |

## 9. Изменённые области (ключевые)

`Views/MainWindow.*`, `Services/Network/*`, `Services/System/RecommendationEngine.cs`, `Services/System/SafeAction*.cs`, `Services/Ui/ShellFit.cs`, `Services/Ui/Loc.cs`, `Services/Ui/UiStateStore.cs`, `Controls/ShellTab*`, `tests/Visor.Tests/*`, `docs/QA-CHECKLIST.md`, `CHANGELOG.md`, `README.md`, `app.manifest` (PerMonitorV2).

## 10. Сборка и тесты

```
dotnet build Visor.sln -c Release   → 0 Warning(s) / 0 Error(s) (после финального прогона)
dotnet test Visor.Tests             → 279 passed, 3 skipped
```

Автоматически покрыто: path security, config, packs, hotkeys, bounds clamp, ShellFit DPI targets, NetworkStats/ranking, SafeAction flow, RecommendationEngine, Loc.

## 11. Визуальные / ручные проверки

| Проверка | Статус |
|----------|--------|
| Unit/integration (`dotnet test`) | ✅ выполнено |
| Код-ревью DPI / 768 heights / clamp / wheel | ✅ выполнено (adversarial agents) |
| Ручной прогон всего `docs/QA-CHECKLIST.md` на ПК с дисплеем | ➖ **не выполнен в этой среде** (приложение меняет дисплей/хуки; чек-лист запрещает «заглушечный» прогон) |
| DPI matrix 100/125/150 на железе | ➖ ожидает человека — пункты §3 QA |

## 12. Известные ограничения

- Ручной QA не закрыт галочками — требуется прогон на реальном Windows-ПК.
- ICMP может быть заблокирован файрволом (UI это объясняет).
- Recommendations не авто-применяются; conflicts — только info/Dismiss.
- `EnsureInsideWorkArea` использует nearest monitor; экстремальные multi-DPI layout edge cases возможны.

## 13. Следующие шаги

1. Человек: пройти `docs/QA-CHECKLIST.md` (особенно §3 DPI/768/clamp) и отметить ✅/❌.  
2. При необходимости — P1 «In-app QA mode».  
3. Релизный `build-release.ps1` / Setup smoke после ручного QA.
