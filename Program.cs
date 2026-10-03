using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// تشغيل قاعدة البيانات المدمجة
builder.Services.AddDbContext<HospitalDb>(opt => opt.UseInMemoryDatabase("AlAmalHospital"));

var app = builder.Build();

// تشغيل دعم ملفات الويب
app.UseDefaultFiles();
app.UseStaticFiles();

// 1. استرجاع قائمة المرضى
app.MapGet("/api/patients", async (HospitalDb db) => await db.Patients.ToListAsync());

// 2. تسجيل مريض جديد
app.MapPost("/api/patients", async (Patient patient, HospitalDb db) => {
    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Created($"/api/patients/{patient.Id}", patient);
});

// 3. مشيد النسخ (Copy Constructor في الويب)
app.MapPost("/api/patients/{id}/clone", async (int id, CloneDto dto, HospitalDb db) => {
    var source = await db.Patients.FindAsync(id);
    if (source is null) return Results.NotFound();
    
    var clone = new Patient {
        Name = dto.NewName,
        Age = source.Age,
        Gender = source.Gender,
        Department = source.Department,
        Condition = dto.NewCondition
    };
    db.Patients.Add(clone);
    await db.SaveChangesAsync();
    return Results.Ok(clone);
});

// 4. دالة حساب الفاتورة (CalculateBill)
app.MapPost("/api/patients/{id}/bill", async (int id, BillRequest req, HospitalDb db) => {
    var p = await db.Patients.FindAsync(id);
    if (p is null) return Results.NotFound();
    
    decimal dailyRate = req.DailyRate ?? 150.0m;
    decimal consultationFee = 50.0m; // رسوم الكشف الأساسية
    decimal total = (req.Days * dailyRate) + consultationFee;

    return Results.Ok(new {
        PatientName = p.Name,
        Days = req.Days,
        DailyRate = dailyRate,
        ConsultationFee = consultationFee,
        Total = total
    });
});

// 5. حذف / تخريج مريض
app.MapDelete("/api/patients/{id}", async (int id, HospitalDb db) => {
    var p = await db.Patients.FindAsync(id);
    if (p is null) return Results.NotFound();
    db.Patients.Remove(p);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// بذر بيانات أولية عند أول تشغيل
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<HospitalDb>();
    if (!db.Patients.Any()) {
        db.Patients.AddRange(
            new Patient { Name = "Ahmed Ali", Age = 35, Gender = "ذكر", Department = "Cardiology", Condition = "Arrhythmia" },
            new Patient { Name = "Saeed Salem", Age = 35, Gender = "ذكر", Department = "Cardiology", Condition = "Stable" },
            new Patient { Name = "فاطمة أحمد", Age = 28, Gender = "أنثى", Department = "Neurology", Condition = "صداع نصفي" }
        );
        db.SaveChanges();
    }
}

app.Run();

// كلاسات النظام وقاعدة البيانات
public class Patient {
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "";
    public string Department { get; set; } = "";
    public string Condition { get; set; } = "";
}

public class HospitalDb : DbContext {
    public HospitalDb(DbContextOptions<HospitalDb> options) : base(options) { }
    public DbSet<Patient> Patients => Set<Patient>();
}

public record CloneDto(string NewName, string NewCondition);
public record BillRequest(int Days, decimal? DailyRate);