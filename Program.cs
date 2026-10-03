using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// قاعدة بيانات مدمجة تحفظ السجلات
builder.Services.AddDbContext<HospitalDbContext>(opt => opt.UseInMemoryDatabase("UniversalHospitalOS"));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 1. استرجاع إعدادات المستشفى
app.MapGet("/api/hospital/config", async (HospitalDbContext db) =>
    Results.Ok(await db.Profiles.FirstOrDefaultAsync() ?? new HospitalProfile()));

// 2. تعديل بيانات المستشفى بدون تغيير الكود (Setup Wizard)
app.MapPut("/api/hospital/config", async (HospitalProfile update, HospitalDbContext db) =>
{
    var p = await db.Profiles.FirstOrDefaultAsync();
    if (p == null) db.Profiles.Add(update);
    else
    {
        p.HospitalName = update.HospitalName;
        p.CurrencySymbol = update.CurrencySymbol;
        p.BaseConsultationFee = update.BaseConsultationFee;
        p.DefaultDailyInpatientRate = update.DefaultDailyInpatientRate;
    }
    await db.SaveChangesAsync();
    return Results.Ok(p ?? update);
});

// 3. إحصائيات لوحة التحكم
app.MapGet("/api/dashboard/stats", async (HospitalDbContext db) =>
{
    return Results.Ok(new
    {
        totalPatients = await db.Patients.CountAsync(),
        activeDepartments = await db.Departments.CountAsync(),
        doctorsOnDuty = await db.Doctors.CountAsync(),
        totalRevenue = await db.Invoices.SumAsync(i => (decimal?)i.TotalAmount) ?? 0m
    });
});

// 4. استرجاع قائمة المرضى
app.MapGet("/api/patients", async (string? search, string? department, HospitalDbContext db) =>
{
    var q = db.Patients.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var s = search.Trim().ToLower();
        q = q.Where(p => p.Name.ToLower().Contains(s) || p.MedicalCondition.ToLower().Contains(s));
    }
    if (!string.IsNullOrWhiteSpace(department) && department != "All")
    {
        q = q.Where(p => p.Department == department);
    }
    return Results.Ok(await q.OrderByDescending(p => p.Id).ToListAsync());
});

// 5. إضافة مريض جديد
app.MapPost("/api/patients", async (Patient patient, HospitalDbContext db) =>
{
    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Created($"/api/patients/{patient.Id}", patient);
});

// 6. نسخ مريض (Copy Constructor)
app.MapPost("/api/patients/{id}/clone", async (int id, CloneDto cmd, HospitalDbContext db) =>
{
    var source = await db.Patients.FindAsync(id);
    if (source == null) return Results.NotFound();

    var clone = new Patient
    {
        Name = cmd.NewName,
        Age = source.Age,
        Gender = source.Gender,
        Department = source.Department,
        MedicalCondition = cmd.NewCondition
    };
    db.Patients.Add(clone);
    await db.SaveChangesAsync();
    return Results.Ok(clone);
});

// 7. دالة الفاتورة الأصلية (CalculateBill)
app.MapPost("/api/patients/{id}/calculate-bill", async (int id, BillDto cmd, HospitalDbContext db) =>
{
    var p = await db.Patients.FindAsync(id);
    if (p == null) return Results.NotFound();

    var profile = await db.Profiles.FirstOrDefaultAsync() ?? new HospitalProfile();
    decimal rate = cmd.CustomDailyRate ?? profile.DefaultDailyInpatientRate;
    decimal consultation = profile.BaseConsultationFee;
    decimal staySubtotal = cmd.NumberOfDays * rate;
    decimal grandTotal = staySubtotal + consultation;

    var inv = new Invoice { PatientId = p.Id, InpatientDays = cmd.NumberOfDays, DailyRate = rate, TotalAmount = grandTotal };
    db.Invoices.Add(inv);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        invoiceNumber = inv.Id,
        patientName = p.Name,
        numberOfDays = cmd.NumberOfDays,
        dailyRate = rate,
        inpatientSubtotal = staySubtotal,
        consultationFee = consultation,
        totalPayable = grandTotal
    });
});

// 8. حذف مريض
app.MapDelete("/api/patients/{id}", async (int id, HospitalDbContext db) =>
{
    var p = await db.Patients.FindAsync(id);
    if (p == null) return Results.NotFound();
    db.Patients.Remove(p);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// بذر بيانات التجربة من الكود القديم
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
    db.Database.EnsureCreated();
    if (!db.Profiles.Any())
    {
        db.Profiles.Add(new HospitalProfile());
        var c = new Department { Name = "Cardiology" };
        var n = new Department { Name = "Neurology" };
        var g = new Department { Name = "General Medicine" };
        db.Departments.AddRange(c, n, g);

        db.Doctors.AddRange(
            new Doctor { FullName = "Dr. Ahmed Mansoor", Specialty = "Cardiologist" },
            new Doctor { FullName = "Dr. Layla Al-Otaibi", Specialty = "Neurologist" }
        );

        db.Patients.AddRange(
            new Patient { Name = "Ahmed Ali", Age = 35, Gender = "Male", Department = "Cardiology", MedicalCondition = "Arrhythmia" },
            new Patient { Name = "Saeed Salem", Age = 35, Gender = "Male", Department = "Cardiology", MedicalCondition = "Stable" },
            new Patient { Name = "Fatima Al-Mansoor", Age = 28, Gender = "Female", Department = "Neurology", MedicalCondition = "Migraine" }
        );
        db.SaveChanges();
    }
}

app.MapFallbackToFile("index.html");
app.Run();

// الكلاسات
public class HospitalProfile {
    public int Id { get; set; }
    public string HospitalName { get; set; } = "Al-Amal Specialized Hospital";
    public string CurrencySymbol { get; set; } = "$";
    public decimal BaseConsultationFee { get; set; } = 50.0m;
    public decimal DefaultDailyInpatientRate { get; set; } = 150.0m;
}
public class Department { public int Id { get; set; } public string Name { get; set; } = ""; }
public class Doctor { public int Id { get; set; } public string FullName { get; set; } = ""; public string Specialty { get; set; } = ""; }
public class Patient {
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "";
    public string Department { get; set; } = "";
    public string MedicalCondition { get; set; } = "";
}
public class Invoice { public int Id { get; set; } public int PatientId { get; set; } public int InpatientDays { get; set; } public decimal DailyRate { get; set; } public decimal TotalAmount { get; set; } }

public class HospitalDbContext : DbContext {
    public HospitalDbContext(DbContextOptions<HospitalDbContext> opt) : base(opt) { }
    public DbSet<HospitalProfile> Profiles => Set<HospitalProfile>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
}

public record CloneDto(string NewName, string NewCondition);
public record BillDto(int NumberOfDays, decimal? CustomDailyRate);
