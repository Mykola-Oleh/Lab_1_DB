using Npgsql;
using University.Dal;

var connString = Environment.GetEnvironmentVariable("UNI_DB")
    ?? "Host=localhost;Port=5432;Database=university;Username=postgres;Password=postgres";

await using var dataSource = NpgsqlDataSource.Create(connString);
var service = new EnrollmentService(() => new UnitOfWork(dataSource));

const long actorUserId = 1;
const long groupId = 1;
const long semesterId = 1;

try
{
    // 1. Читання через view
    await using (var uow = new UnitOfWork(dataSource))
    {
        Console.WriteLine("=== Студенти групи (v_students_full) ===");
        foreach (var s in await uow.Students.GetByGroupAsync(groupId))
            Console.WriteLine($"{s.StudentId}: {s.FullName} | {s.GroupName} | {s.FacultyName}");
    }

    // 2. Додавання студента в транзакції
    long newStudentId;
    await using (var uow = new UnitOfWork(dataSource))
    {
        await uow.BeginAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        newStudentId = await uow.Students.AddAsync(new NewStudent(
            groupId, "Андрій", "Мельник", null, $"melnyk.{suffix}@uni.ua",
            new DateOnly(2005, 1, 10), $"KV-{suffix}"), actorUserId);
        await uow.CommitAsync();
        Console.WriteLine($"\nДодано студента id={newStudentId}");
    }

    // 3. Зарахування на кілька курсів однією транзакцією
    var enrollmentIds = await service.EnrollOnCoursesAsync(newStudentId, [1, 2], semesterId, actorUserId);
    Console.WriteLine($"Зарахування: {string.Join(", ", enrollmentIds)}");

    // 4. Порушення бізнес-правила: повторне зарахування
    try
    {
        await service.EnrollOnCoursesAsync(newStudentId, [1], semesterId, actorUserId);
    }
    catch (BusinessRuleException ex)
    {
        Console.WriteLine($"Очікувана відмова: {ex.Message}");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"Очікувана відмова: {ex.Message}");
    }

    // 5. Завантаженість курсів і функція середнього бала
    await using (var uow = new UnitOfWork(dataSource))
    {
        Console.WriteLine("\n=== Завантаженість курсів (v_course_load) ===");
        foreach (var c in await uow.Courses.GetLoadAsync(semesterId))
            Console.WriteLine($"{c.Code,-7} {c.Title,-22} зайнято {c.Enrolled}/{c.MaxCapacity}, вільно {c.FreeSeats}");

        var avg = await uow.Students.GetAverageScoreAsync(newStudentId, null);
        Console.WriteLine($"\nСередній бал студента {newStudentId}: {(avg?.ToString() ?? "оцінок немає")}");
    }

    // 6. М'яке видалення
    await using (var uow = new UnitOfWork(dataSource))
    {
        await uow.BeginAsync();
        await uow.Students.SoftDeleteAsync(newStudentId, actorUserId);
        await uow.CommitAsync();
        Console.WriteLine($"Студента {newStudentId} видалено (soft delete)");
    }
}
catch (BusinessRuleException ex)
{
    Console.WriteLine($"Порушено бізнес-правило: {ex.Message}");
}
catch (NpgsqlException ex)
{
    Console.WriteLine($"Помилка БД: {ex.Message}");
}