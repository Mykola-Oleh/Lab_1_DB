
// 04_UniversityDal.cs  |  .NET 8, Npgsql 8, Dapper 2
// dotnet add package Npgsql ; dotnet add package Dapper
//
// Repository + Unit of Work. ПРАВИЛО: код застосунку НЕ містить прямих
// SELECT/INSERT/UPDATE по таблицях — лише
//   * CALL sp_...(...)            — збережені процедури (зміна даних)
//   * SELECT ... FROM v_...       — представлення (читання)
//   * SELECT fn_...(...)          — користувацькі функції (скалярні обчислення)

using Dapper;
using Npgsql;
using System.Data;

namespace University.Dal;


public sealed record StudentFullDto(
    long StudentId, string StudentCardNo, string FullName, string Email, string Status,
    long GroupId, string GroupName, string SpecialityName, string FacultyName);

public sealed record CourseLoadDto(
    long CourseId, string Code, string Title, int MaxCapacity, long? SemesterId, int Enrolled, int FreeSeats);

public sealed record NewStudent(
    long GroupId, string FirstName, string LastName, string? MiddleName,
    string Email, DateOnly BirthDate, string CardNo);

public sealed class BusinessRuleException(string message, Exception inner) : Exception(message, inner);

public interface IStudentRepository
{
    Task<long> AddAsync(NewStudent s, long actorUserId, CancellationToken ct = default);
    Task ChangeGroupAsync(long studentId, long newGroupId, long actorUserId, CancellationToken ct = default);
    Task SoftDeleteAsync(long studentId, long actorUserId, CancellationToken ct = default);
    Task<IReadOnlyList<StudentFullDto>> GetByGroupAsync(long groupId, CancellationToken ct = default);
    Task<decimal?> GetAverageScoreAsync(long studentId, long? semesterId, CancellationToken ct = default);
}

public interface ICourseRepository
{
    Task<int> GetFreeSeatsAsync(long courseId, long semesterId, CancellationToken ct = default);
    Task<IReadOnlyList<CourseLoadDto>> GetLoadAsync(long semesterId, CancellationToken ct = default);
}

public interface IEnrollmentRepository
{
    Task<long> EnrollAsync(long studentId, long courseId, long semesterId, long actorUserId, CancellationToken ct = default);
}

public interface IUnitOfWork : IAsyncDisposable
{
    IStudentRepository Students { get; }
    ICourseRepository Courses { get; }
    IEnrollmentRepository Enrollments { get; }

    Task BeginAsync(IsolationLevel level = IsolationLevel.ReadCommitted, CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}


// Базовий репозиторій: єдине місце виконання SQL; усі репозиторії
// ділять одне з'єднання та транзакцію Unit of Work.

public abstract class RepositoryBase(DbSession session)
{
    protected async Task<T> CallProcAsync<T>(string call, object args, CancellationToken ct)
    {
        try
        {
            var cmd = new CommandDefinition(call, args, session.Transaction, cancellationToken: ct);
            return await session.Connection.ExecuteScalarAsync<T>(cmd) ?? throw new InvalidOperationException("Procedure returned no value");
        }
        catch (PostgresException ex) { throw Map(ex); }
    }

    protected async Task CallProcAsync(string call, object args, CancellationToken ct)
    {
        try
        {
            await session.Connection.ExecuteAsync(new CommandDefinition(call, args, session.Transaction, cancellationToken: ct));
        }
        catch (PostgresException ex) { throw Map(ex); }
    }

    protected async Task<IReadOnlyList<T>> QueryViewAsync<T>(string selectFromView, object args, CancellationToken ct) =>
        (await session.Connection.QueryAsync<T>(new CommandDefinition(selectFromView, args, session.Transaction, cancellationToken: ct))).AsList();

    protected async Task<T?> ScalarFunctionAsync<T>(string select, object args, CancellationToken ct) =>
        await session.Connection.ExecuteScalarAsync<T>(new CommandDefinition(select, args, session.Transaction, cancellationToken: ct));

    private static Exception Map(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.RaiseException
            ? new BusinessRuleException(ex.MessageText, ex)
            : ex;
}

public sealed class DbSession(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    public NpgsqlConnection Connection => _connection ??= dataSource.OpenConnection();
    public NpgsqlTransaction? Transaction { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null) await Transaction.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

// Репозиторії

public sealed class StudentRepository(DbSession s) : RepositoryBase(s), IStudentRepository
{
    public Task<long> AddAsync(NewStudent n, long actor, CancellationToken ct) =>
        CallProcAsync<long>(
            "CALL sp_add_student(@GroupId::bigint, @FirstName::varchar, @LastName::varchar, @MiddleName::varchar, @Email::varchar, " +
            "@BirthDate::date, @CardNo::varchar, @Actor::bigint, NULL::bigint)",
            new
            {
                n.GroupId,
                n.FirstName,
                n.LastName,
                n.MiddleName,
                n.Email,
                BirthDate = n.BirthDate.ToDateTime(TimeOnly.MinValue),
                n.CardNo,
                Actor = actor
            }, ct);

    public Task ChangeGroupAsync(long studentId, long newGroupId, long actor, CancellationToken ct) =>
        CallProcAsync("CALL sp_update_student_group(@studentId::bigint, @newGroupId::bigint, @actor::bigint)",
                      new { studentId, newGroupId, actor }, ct);

    public Task SoftDeleteAsync(long studentId, long actor, CancellationToken ct) =>
        CallProcAsync("CALL sp_soft_delete_student(@studentId::bigint, @actor::bigint)", new { studentId, actor }, ct);

    public Task<IReadOnlyList<StudentFullDto>> GetByGroupAsync(long groupId, CancellationToken ct) =>
        QueryViewAsync<StudentFullDto>(
            "SELECT student_id, student_card_no, full_name, email, status, group_id, group_name, speciality_name, faculty_name " +
            "FROM v_students_full WHERE group_id = @groupId ORDER BY full_name", new { groupId }, ct);

    public Task<decimal?> GetAverageScoreAsync(long studentId, long? semesterId, CancellationToken ct) =>
        ScalarFunctionAsync<decimal?>("SELECT fn_student_avg_score(@studentId::bigint, @semesterId::bigint)", new { studentId, semesterId }, ct);
}

public sealed class CourseRepository(DbSession s) : RepositoryBase(s), ICourseRepository
{
    public async Task<int> GetFreeSeatsAsync(long courseId, long semesterId, CancellationToken ct) =>
        await ScalarFunctionAsync<int?>("SELECT fn_course_free_seats(@courseId::bigint, @semesterId::bigint)", new { courseId, semesterId }, ct) ?? 0;

    public Task<IReadOnlyList<CourseLoadDto>> GetLoadAsync(long semesterId, CancellationToken ct) =>
        QueryViewAsync<CourseLoadDto>(
            "SELECT course_id, code, title, max_capacity, semester_id, enrolled::int, free_seats::int " +
            "FROM v_course_load WHERE semester_id = @semesterId OR semester_id IS NULL ORDER BY title", new { semesterId }, ct);
}

public sealed class EnrollmentRepository(DbSession s) : RepositoryBase(s), IEnrollmentRepository
{
    public Task<long> EnrollAsync(long studentId, long courseId, long semesterId, long actor, CancellationToken ct) =>
        CallProcAsync<long>("CALL sp_enroll_student(@studentId::bigint, @courseId::bigint, @semesterId::bigint, @actor::bigint, NULL::bigint)",
                            new { studentId, courseId, semesterId, actor }, ct);
}

// Unit of Work
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly DbSession _session;

    public UnitOfWork(NpgsqlDataSource dataSource)
    {
        _session = new DbSession(dataSource);
        Students = new StudentRepository(_session);
        Courses = new CourseRepository(_session);
        Enrollments = new EnrollmentRepository(_session);
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public IStudentRepository Students { get; }
    public ICourseRepository Courses { get; }
    public IEnrollmentRepository Enrollments { get; }

    public async Task BeginAsync(IsolationLevel level = IsolationLevel.ReadCommitted, CancellationToken ct = default) =>
        _session.Transaction ??= await _session.Connection.BeginTransactionAsync(level, ct);

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_session.Transaction is null) return;
        await _session.Transaction.CommitAsync(ct);
        await _session.Transaction.DisposeAsync();
        _session.Transaction = null;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_session.Transaction is null) return;
        await _session.Transaction.RollbackAsync(ct);
        await _session.Transaction.DisposeAsync();
        _session.Transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session.Transaction is not null) await RollbackAsync();
        await _session.DisposeAsync();
    }
}

// Сервіс: транзакція, що охоплює декілька сутностей
// (студент + група + курс + зарахування). Перевірка лімітів виконується
// в sp_enroll_student під блокуванням рядка курсу (FOR UPDATE), тому
// навіть при паралельних запитах місць не буде більше, ніж max_capacity.

public sealed class EnrollmentService(Func<IUnitOfWork> uowFactory)
{
    public async Task<IReadOnlyList<long>> EnrollOnCoursesAsync(
        long studentId, IEnumerable<long> courseIds, long semesterId, long actorUserId, CancellationToken ct = default)
    {
        await using var uow = uowFactory();
        await uow.BeginAsync(ct: ct);
        try
        {
            var ids = new List<long>();
            foreach (var courseId in courseIds)
            {
                if (await uow.Courses.GetFreeSeatsAsync(courseId, semesterId, ct) <= 0)
                    throw new InvalidOperationException($"Курс {courseId}: немає вільних місць");

                ids.Add(await uow.Enrollments.EnrollAsync(studentId, courseId, semesterId, actorUserId, ct));
            }
            await uow.CommitAsync(ct);
            return ids;
        }
        catch
        {
            await uow.RollbackAsync(ct);
            throw;
        }
    }

    public async Task TransferAndEnrollAsync(
        long studentId, long newGroupId, IEnumerable<long> courseIds, long semesterId, long actorUserId, CancellationToken ct = default)
    {
        await using var uow = uowFactory();
        await uow.BeginAsync(ct: ct);
        try
        {
            await uow.Students.ChangeGroupAsync(studentId, newGroupId, actorUserId, ct);
            foreach (var courseId in courseIds)
                await uow.Enrollments.EnrollAsync(studentId, courseId, semesterId, actorUserId, ct);
            await uow.CommitAsync(ct);
        }
        catch { await uow.RollbackAsync(ct); throw; }
    }
}

