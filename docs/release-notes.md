## Snowdrift 1.1.3

- **Плавные жесты для Windows убраны.** Snowdrift снова только про снег. Если вы выключали жесты Windows для сенсорной панели ради Snowdrift, после обновления он один раз напомнит об этом и по клику откроет настройки, чтобы включить их обратно: «Параметры → Bluetooth и устройства → Сенсорная панель → Жесты тремя / четырьмя пальцами».
- Обновления остаются: Snowdrift обновляется сам, а кнопка **«⬆ Обновить Snowdrift»** стоит первой в меню.

### Установка и обновление одной командой
**Windows** (PowerShell, `Win+X` → «Терминал»):
```powershell
irm https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.ps1 | iex
```
**macOS** («Терминал»):
```bash
curl -fsSL https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.sh | bash
```

### Файлы
| Система | Файл |
| --- | --- |
| Windows 10 / 11 (обычные ноутбуки и ПК) | `Snowdrift-Windows-x64.zip` |
| Windows на ARM (Snapdragon) | `Snowdrift-Windows-ARM64.zip` |
| macOS 14+ | `Snowdrift-macOS.zip` |

`SHA256SUMS.txt` — контрольные суммы архивов.

---

**English:** the Windows touchpad gestures are removed (a one-time note helps switch Windows' own swipes back on); automatic updates and the "⬆ Update Snowdrift" button stay.
