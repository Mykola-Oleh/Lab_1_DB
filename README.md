# University DB — система управління університетом

Навчальний проєкт: реляційна БД на **PostgreSQL** (18 таблиць, soft delete, аудит, процедури, функції, тригери, представлення, індекси) і консольний застосунок на **C# / .NET** з патернами **Repository + Unit of Work**. Весь доступ застосунку до даних іде лише через збережені процедури та представлення.

Опис проєктування (ER-діаграма, зв'язки, пояснення індексів, DBML) — у [DESIGN.md](DESIGN.md).

## Структура проєкту

```
university/
├── 01_schema.sql        # таблиці, PK/FK, CHECK, DEFAULT, UNIQUE
├── 02_objects.sql       # функції, тригери, процедури, представлення
├── 03_indexes.sql       # B-Tree, partial, GIN (FTS + trigram), BRIN
├── 05_seed.sql          # тестові дані для демонстрації
├── 04_UniversityDal.cs  # копія DAL-коду (еталон; робоча копія — в App/)
├── DESIGN.md            # проєктування БД
└── App/
    ├── University.csproj
    ├── Program.cs       # демонстраційний сценарій
    └── UniversityDal.cs # DTO, репозиторії, UnitOfWork, EnrollmentService
```

## Вимоги

- Docker Desktop (або локальний PostgreSQL 14+)
- .NET SDK 8.0 або новіший (працює й на 10.0)
- NuGet-пакети підтягуються автоматично: `Npgsql`, `Dapper`

## Швидкий старт

### 1. База даних у Docker

```powershell
docker run -d --name university-db `
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=university `
  -p 5432:5432 -v university_pgdata:/var/lib/postgresql/data postgres:16
```

### 2. Створення схеми й тестових даних

Виконується строго в такому порядку:

```powershell
foreach ($f in "01_schema","02_objects","03_indexes","05_seed") {
  docker cp "$f.sql" university-db:/tmp/
  docker exec -i university-db psql -U postgres -d university -v ON_ERROR_STOP=1 -f "/tmp/$f.sql"
}
```

Перевірка: `docker exec -it university-db psql -U postgres -d university -c "\dt"` — має бути 18 таблиць.

### 3. Запуск застосунку

```powershell
cd App
dotnet run
```

Рядок підключення за замовчуванням: `Host=localhost;Port=5432;Database=university;Username=postgres;Password=postgres`. Щоб змінити, задайте змінну середовища:

```powershell
$env:UNI_DB = "Host=localhost;Port=5433;Database=university;Username=postgres;Password=secret"
```

## Що показує демонстрація (`Program.cs`)

| Крок | Дія | Об'єкт БД |
|---|---|---|
| 1 | Список студентів групи | `v_students_full` |
| 2 | Додавання студента | `sp_add_student` |
| 3 | Зарахування на 2 курси однією транзакцією | `sp_enroll_student` ×2 |
| 4 | Повторне зарахування → відмова | перевірка в `sp_enroll_student` |
| 5 | Завантаженість курсів, середній бал | `v_course_load`, `fn_student_avg_score` |
| 6 | М'яке видалення студента | `sp_soft_delete_student` |

Кожен запуск створює нового студента з унікальним email і одразу м'яко видаляє його, тому демо можна повторювати.

## Архітектура коду

```
EnrollmentService            бізнес-сценарії, керує транзакцією
        │
   IUnitOfWork ──── Begin / Commit / Rollback
        │
        ├── IStudentRepository     → sp_add_student, sp_update_student_group,
        │                            sp_soft_delete_student, v_students_full, fn_student_avg_score
        ├── ICourseRepository      → v_course_load, fn_course_free_seats
        └── IEnrollmentRepository  → sp_enroll_student
        │
   RepositoryBase                  єдине місце виконання SQL
        │
   DbSession (одне з'єднання + одна транзакція на UnitOfWork)
```

- **Unit of Work** створює одне з'єднання й одну транзакцію, спільні для всіх репозиторіїв. Якщо `Commit` не викликано, при `Dispose` транзакція відкочується.
- **Repository** не містить прямих `SELECT/INSERT/UPDATE` по таблицях. Доступні лише три шляхи: `CALL sp_*` (зміна), `SELECT … FROM v_*` (читання), `SELECT fn_*` (скалярні обчислення).
- **Помилки бізнес-правил** (`RAISE EXCEPTION` у процедурах, SQLSTATE `P0001`) перетворюються на `BusinessRuleException` з текстом із БД.
- **INOUT-параметри:** процедури повертають згенерований id. Місце під нього в `CALL` займає `NULL::bigint`, значення читається як результат запиту.
- **Типи параметрів** приведені явно (`@BirthDate::date`, `@id::bigint`), інакше PostgreSQL не знаходить процедуру за сигнатурою.

### Приклад використання

```csharp
var ds = NpgsqlDataSource.Create(connString);
var service = new EnrollmentService(() => new UnitOfWork(ds));

try
{
    // все або нічого: якщо один з курсів не підходить — відкат усіх
    var ids = await service.EnrollOnCoursesAsync(studentId: 1, courseIds: [1, 2], semesterId: 1, actorUserId: 1);
}
catch (BusinessRuleException ex)
{
    Console.WriteLine(ex.Message);   // напр. "На курсі немає вільних місць (2 / 2)"
}
```

## Правила, які забезпечує БД

- Зарахування: студент активний, немає дубліката, є вільні місця (рядок курсу блокується `FOR UPDATE`), сума кредитів семестру ≤ 30.
- Розклад (`sp_add_schedule_entry`): немає конфліктів аудиторії, викладача та групи; місткість аудиторії достатня.
- Видалення викладача заборонене, поки він завідує кафедрою або має заняття в розкладі; групи — поки в ній є студенти.
- Фізичний `DELETE` у ключових таблицях автоматично перетворюється на soft delete.
- Усі зміни студентів, викладачів, оцінок і зарахувань потрапляють в `audit_logs`.

## Перевірка результату в БД

```sql
SELECT student_id, last_name, is_deleted, deleted_at, updated_at, updated_by_user_id FROM students;
SELECT table_name, record_id, action, performed_at FROM audit_logs ORDER BY audit_id DESC LIMIT 10;
SELECT * FROM v_course_load;
```

Для перегляду БД у браузері можна запустити Adminer: `docker run -d --name adminer -p 8080:8080 adminer` → http://localhost:8080 (сервер `host.docker.internal`, користувач `postgres`).

## Часті проблеми

| Симптом | Причина / рішення |
|---|---|
| `procedure sp_... does not exist` | Не виконано скрипти в порядку 01 → 02 → 03 → 05 або перед `dotnet run` змінено типи параметрів без приведення (`::date`, `::bigint`) |
| `?` замість українських літер у консолі | У `Program.cs` має бути `Console.OutputEncoding = Encoding.UTF8;` |
| `connection refused` | Контейнер не запущено (`docker start university-db`) або порт 5432 зайнятий — запустіть з `-p 5433:5432` і вкажіть `Port=5433` |
| `Група ... не існує` / `Студент ... неактивний` | Не виконано `05_seed.sql` або використано неіснуючі id |
| Скрипти потрібно виконати повторно | `docker exec -it university-db psql -U postgres -c "DROP DATABASE university" -c "CREATE DATABASE university"`, потім кроки 2–3 |

## Корисні команди Docker

```powershell
docker stop university-db        # зупинити
docker start university-db       # запустити (дані збережуться)
docker rm -f university-db       # видалити контейнер
docker volume rm university_pgdata   # видалити дані
```
