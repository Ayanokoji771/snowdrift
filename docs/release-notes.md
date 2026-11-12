## Snowdrift 1.1.1

### Установка и обновление одной командой
**Windows** (PowerShell, `Win+X` → «Терминал»):
```powershell
irm https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.ps1 | iex
```
**macOS** («Терминал»):
```bash
curl -fsSL https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.sh | bash
```
Одна и та же команда и ставит, и обновляет. Она скачивает этот выпуск, сверяет контрольную сумму SHA-256, кладёт программу на то же место и запускает её. Настройки сохраняются, права администратора не нужны. Если у вас 1.0.0, выполните команду один раз, и старая версия заменится.

### Обновление в один клик
- Snowdrift раз в день проверяет, нет ли новой версии. На Windows появится всплывающее сообщение, а на Mac — стрелочка ↑ у снежинки. В меню будет пункт «⬆ Обновить до …».
- Один клик: Snowdrift скачает новую версию, сверит контрольную сумму, перезапустится и сохранит настройки.
- Проверка стала надёжнее: теперь она идёт без GitHub API, у которого есть лимит запросов. Раньше она могла не работать там, где у многих людей один общий адрес в интернете (мобильный интернет, офис).

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

**English:** install or update with one command (`irm …/install.ps1 | iex` on Windows, `curl -fsSL …/install.sh | bash` on macOS); one-click in-app updates that no longer depend on the rate-limited GitHub API; Mac-smooth touchpad gestures on Windows.
