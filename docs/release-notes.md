## Snowdrift 1.1.2

### Обновляется сам ⬆
- Новые версии ставятся сами: Snowdrift скачивает их, сверяет контрольную сумму SHA-256 и перезапускается, когда вы три минуты не трогаете компьютер. Настройки сохраняются. Выключить можно в меню «Обновления» → «Обновлять автоматически».
- Кнопка **«⬆ Обновить Snowdrift»** всегда стоит первой в меню. Один клик — и Snowdrift проверит GitHub, обновится и перезапустится.
- Windows-версия стала меньше на 6 МБ.

### Установка и обновление одной командой
**Windows** (PowerShell, `Win+X` → «Терминал»):
```powershell
irm https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.ps1 | iex
```
**macOS** («Терминал»):
```bash
curl -fsSL https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.sh | bash
```
Одна команда и ставит, и обновляет: на месте, с сохранением настроек и без прав администратора. На Windows видно, сколько скачано и что происходит. Версия для Windows весит около 67 МБ, потому что внутри всё нужное для работы. Если у вас 1.0.0–1.1.1, выполните команду один раз, дальше Snowdrift будет обновляться сам.

### Плавные жесты как на Mac ✋ (Windows, сенсорная панель)
- Свайп четырьмя или тремя пальцами вбок — рабочий стол едет вслед за пальцами и мягко докатывается на пружинке. Свайп вниз — окна уходят в панель задач, свайп вверх — возвращаются.
- Как включить: меню → «Плавные жесты как на Mac ✋» → «Включить: отключить жесты Windows…».

### Файлы
| Система | Файл |
| --- | --- |
| Windows 10 / 11 (обычные ноутбуки и ПК) | `Snowdrift-Windows-x64.zip` |
| Windows на ARM (Snapdragon) | `Snowdrift-Windows-ARM64.zip` |
| macOS 14+ | `Snowdrift-macOS.zip` |

`SHA256SUMS.txt` — контрольные суммы архивов.

---

**English:** Snowdrift now updates itself (downloads, verifies SHA-256, restarts while you're away; can be switched off), with an always-visible "⬆ Update Snowdrift" button at the top of the menu. One-command install/update shows its progress; the Windows download is 6 MB smaller.
