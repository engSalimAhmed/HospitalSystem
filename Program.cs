using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// قاعدة بيانات السيرفر الكاملة
builder.Services.AddDbContext<UniversalHospitalDb>(opt => opt.UseInMemoryDatabase("UniversalHospitalOS_Master"));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ==========================================
// 1. HOSPITAL CONFIGURATION & BRANDING (Setup Wizard)
// ==========================================
app.MapGet("/api/config", async (UniversalHospitalDb db) => 
    Results.Ok(await db.HospitalConfigs.FirstOrDefaultAsync() ?? new HospitalConfigEntity()));

app.MapPut("/api/config", async (HospitalConfigEntity update, UniversalHospitalDb db) =>
{
    var config = await db.HospitalConfigs.FirstOrDefaultAsync();
    if (config == null) { db.HospitalConfigs.Add(update); }
    else
    {
        config.HospitalName = update.HospitalName;
        config.Tagline = update.Tagline;
        config.PrimaryColor = update.PrimaryColor;
        config.SecondaryColor = update.SecondaryColor;
        config.Currency = update.Currency;
        config.CurrencySymbol = update.CurrencySymbol;
        config.BaseConsultationFee = update.BaseConsultationFee;
        config.DefaultDailyInpatientRate = update.DefaultDailyInpatientRate;
        config.EmergencyPhone = update.EmergencyPhone;
        config.Address = update.Address;
        config.EnablePharmacy = update.EnablePharmacy;
        config.EnableLaboratory = update.EnableLaboratory;
        config.EnableHomeNursing = update.EnableHomeNursing;
        config.EnableDelivery = update.EnableDelivery;
    }
    await db.SaveChangesAsync();
    return Results.Ok(config ?? update);
});

// ==========================================
// 2. DASHBOARD TELEMETRY (Real database analytics)
// ==========================================
app.MapGet("/api/dashboard/stats", async (UniversalHospitalDb db) =>
{
    var totalPatients = await db.Patients.CountAsync();
    var totalDoctors = await db.Doctors.CountAsync();
    var doctorsOnDuty = await db.Doctors.CountAsync(d => d.IsOnDuty);
    var totalDepts = await db.Departments.CountAsync();
    var totalMeds = await db.Medicines.CountAsync();
    var lowStockMeds = await db.Medicines.CountAsync(m => m.StockQuantity <= m.MinStock);
    var pendingLabs = await db.LabOrders.CountAsync(l => l.Status != "Completed");
    var totalRevenue = await db.Invoices.SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;
    var emergencyCases = await db.Patients.CountAsync(p => p.Department == "Emergency");

    return Results.Ok(new
    {
        totalPatients,
        totalDoctors,
        doctorsOnDuty,
        totalDepts,
        totalMeds,
        lowStockMeds,
        pendingLabs,
        totalRevenue,
        emergencyCases
    });
});

// ==========================================
// 3. PATIENTS & LEGACY METHODS (CalculateBill, Clone)
// ==========================================
app.MapGet("/api/patients", async (string? search, string? department, UniversalHospitalDb db) =>
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

app.MapPost("/api/patients", async (PatientEntity patient, UniversalHospitalDb db) =>
{
    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Created($"/api/patients/{patient.Id}", patient);
});

// Copy Constructor (Legacy Preserved)
app.MapPost("/api/patients/{id}/clone", async (int id, CloneReq req, UniversalHospitalDb db) =>
{
    var source = await db.Patients.FindAsync(id);
    if (source == null) return Results.NotFound();

    var clone = new PatientEntity
    {
        Name = req.NewName,
        Age = source.Age,
        Gender = source.Gender,
        Department = source.Department,
        MedicalCondition = req.NewCondition,
        BloodType = source.BloodType,
        Phone = source.Phone
    };
    db.Patients.Add(clone);
    await db.SaveChangesAsync();
    return Results.Ok(clone);
});

// CalculateBill Unified Calculation Engine
app.MapPost("/api/patients/{id}/calculate-bill", async (int id, BillPayload payload, UniversalHospitalDb db) =>
{
    var p = await db.Patients.FindAsync(id);
    if (p == null) return Results.NotFound();

    var config = await db.HospitalConfigs.FirstOrDefaultAsync() ?? new HospitalConfigEntity();
    decimal rate = payload.DailyRate ?? config.DefaultDailyInpatientRate;
    decimal consultation = payload.IncludeConsultation ? config.BaseConsultationFee : 0m;
    decimal staySubtotal = payload.NumberOfDays * rate;
    decimal addCharges = payload.PharmacyAmount + payload.LabAmount + payload.ServiceAmount;
    decimal total = (staySubtotal + consultation + addCharges) - payload.Discount;

    var inv = new InvoiceEntity
    {
        PatientId = p.Id,
        InpatientDays = payload.NumberOfDays,
        DailyRate = rate,
        ConsultationFee = consultation,
        PharmacyCharges = payload.PharmacyAmount,
        LabCharges = payload.LabAmount,
        ServiceCharges = payload.ServiceAmount,
        Discount = payload.Discount,
        TotalAmount = total
    };
    db.Invoices.Add(inv);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        invoiceNumber = inv.InvoiceNumber,
        patientName = p.Name,
        inpatientDays = payload.NumberOfDays,
        dailyRate = rate,
        consultationFee = consultation,
        additionalCharges = addCharges,
        discount = payload.Discount,
        totalPayable = total,
        currency = config.CurrencySymbol
    });
});

app.MapDelete("/api/patients/{id}", async (int id, UniversalHospitalDb db) =>
{
    var p = await db.Patients.FindAsync(id);
    if (p == null) return Results.NotFound();
    db.Patients.Remove(p);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ==========================================
// 4. DEPARTMENTS & SERVICES
// ==========================================
app.MapGet("/api/departments", async (UniversalHospitalDb db) => await db.Departments.ToListAsync());
app.MapPost("/api/departments", async (DepartmentEntity dept, UniversalHospitalDb db) =>
{
    db.Departments.Add(dept);
    await db.SaveChangesAsync();
    return Results.Created($"/api/departments/{dept.Id}", dept);
});

// ==========================================
// 5. PHARMACY & COSMETICS
// ==========================================
app.MapGet("/api/pharmacy", async (UniversalHospitalDb db) => await db.Medicines.ToListAsync());
app.MapPost("/api/pharmacy", async (MedicineEntity med, UniversalHospitalDb db) =>
{
    db.Medicines.Add(med);
    await db.SaveChangesAsync();
    return Results.Created($"/api/pharmacy/{med.Id}", med);
});

// ==========================================
// 6. LABORATORY (CBC, Lipid, etc.)
// ==========================================
app.MapGet("/api/laboratory/tests", async (UniversalHospitalDb db) => await db.LabTests.ToListAsync());
app.MapGet("/api/laboratory/orders", async (UniversalHospitalDb db) => await db.LabOrders.ToListAsync());
app.MapPost("/api/laboratory/orders", async (LabOrderEntity order, UniversalHospitalDb db) =>
{
    db.LabOrders.Add(order);
    await db.SaveChangesAsync();
    return Results.Created($"/api/laboratory/orders/{order.Id}", order);
});

// ==========================================
// 7. HOME NURSING & DELIVERY
// ==========================================
app.MapGet("/api/nursing", async (UniversalHospitalDb db) => await db.HomeNursingRequests.ToListAsync());
app.MapPost("/api/nursing", async (HomeNursingRequestEntity req, UniversalHospitalDb db) =>
{
    db.HomeNursingRequests.Add(req);
    await db.SaveChangesAsync();
    return Results.Created($"/api/nursing/{req.Id}", req);
});

// ==========================================
// SEEDING STARTER LIBRARIES & LEGACY PARITY
// ==========================================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UniversalHospitalDb>();
    db.Database.EnsureCreated();

    if (!db.HospitalConfigs.Any())
    {
        // 1. Config
        db.HospitalConfigs.Add(new HospitalConfigEntity());

        // 2. Departments
        db.Departments.AddRange(
            new DepartmentEntity { Name = "Cardiology", Icon = "fa-solid fa-heart-pulse", Description = "Invasive & Non-Invasive Cardiac Care" },
            new DepartmentEntity { Name = "Neurology", Icon = "fa-solid fa-brain", Description = "Neuro-diagnostics & Brain Surgery" },
            new DepartmentEntity { Name = "Emergency", Icon = "fa-solid fa-truck-medical", Description = "24/7 Level 1 Trauma Resuscitation", IsEmergency247 = true },
            new DepartmentEntity { Name = "Dental Clinic", Icon = "fa-solid fa-tooth", Description = "Oral Surgery & Cosmetic Dentistry" },
            new DepartmentEntity { Name = "Dermatology & Cosmetics", Icon = "fa-solid fa-wand-magic-sparkles", Description = "Skin Care & Laser Medicine" }
        );

        // 3. Doctors
        db.Doctors.AddRange(
            new DoctorEntity { FullName = "Dr. Ahmed Mansoor", Specialty = "Cardiologist", Department = "Cardiology", Shift = "08:00 - 16:00", IsOnDuty = true },
            new DoctorEntity { FullName = "Dr. Layla Al-Otaibi", Specialty = "Chief Neurologist", Department = "Neurology", Shift = "16:00 - 00:00", IsOnDuty = true },
            new DoctorEntity { FullName = "Dr. Zaid Al-Hamad", Specialty = "Emergency Specialist", Department = "Emergency", Shift = "24/7 On-Call", IsOnDuty = true }
        );

        // 4. Patients (Preserving original console data)
        db.Patients.AddRange(
            new PatientEntity { Name = "Ahmed Ali", Age = 35, Gender = "Male", Department = "Cardiology", MedicalCondition = "Arrhythmia", BloodType = "O+" },
            new PatientEntity { Name = "Saeed Salem", Age = 35, Gender = "Male", Department = "Cardiology", MedicalCondition = "Stable", BloodType = "O+" },
            new PatientEntity { Name = "Fatima Al-Mansoor", Age = 28, Gender = "Female", Department = "Neurology", MedicalCondition = "Migraine with Aura", BloodType = "A+" }
        );

        // 5. Pharmacy (Medicines + Cosmetics)
        db.Medicines.AddRange(
            new MedicineEntity { Name = "Atorvastatin 20mg", Category = "Heart", SellingPrice = 24.50m, StockQuantity = 110, MinStock = 20 },
            new MedicineEntity { Name = "Panadol Extra", Category = "Pain Relief", SellingPrice = 6.00m, StockQuantity = 12, MinStock = 25 },
            new MedicineEntity { Name = "Hyaluronic Acid Serum", Category = "Cosmetics", SellingPrice = 45.00m, StockQuantity = 30, IsCosmetic = true }
        );

        // 6. Lab Tests Starter Library (CBC, Lipid, etc.)
        db.LabTests.AddRange(
            new LabTestEntity { Name = "Complete Blood Count (CBC)", Category = "Hematology", Price = 25.00m, SampleType = "Whole Blood" },
            new LabTestEntity { Name = "Lipid Profile Test", Category = "Biochemistry", Price = 40.00m, SampleType = "Serum" },
            new LabTestEntity { Name = "Fasting Blood Sugar (Glucose)", Category = "Diabetes", Price = 15.00m, SampleType = "Plasma" }
        );

        db.SaveChanges();
    }
}

app.MapFallbackToFile("index.html");
app.Run();

// ==========================================
// DATA MODELS FOR UNIVERSAL PLATFORM
// ==========================================
public class HospitalConfigEntity
{
    public int Id { get; set; }
    public string HospitalName { get; set; } = "Al-Amal Specialized Hospital";
    public string Tagline { get; set; } = "Universal Clinical Operating System";
    public string PrimaryColor { get; set; } = "#38bdf8";
    public string SecondaryColor { get; set; } = "#6366f1";
    public string Currency { get; set; } = "USD";
    public string CurrencySymbol { get; set; } = "$";
    public decimal BaseConsultationFee { get; set; } = 50.0m;
    public decimal DefaultDailyInpatientRate { get; set; } = 150.0m;
    public string EmergencyPhone { get; set; } = "+966 11 000 0000";
    public string Address { get; set; } = "Medical District, Boulevard 10";
    public bool EnablePharmacy { get; set; } = true;
    public bool EnableLaboratory { get; set; } = true;
    public bool EnableHomeNursing { get; set; } = true;
    public bool EnableDelivery { get; set; } = true;
}

public class PatientEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "Male";
    public string Department { get; set; } = "General";
    public string MedicalCondition { get; set; } = "";
    public string BloodType { get; set; } = "O+";
    public string Phone { get; set; } = "+966 50 000 0000";
    public DateTime AdmittedAt { get; set; } = DateTime.UtcNow;
}

public class DoctorEntity
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string Department { get; set; } = "";
    public string Shift { get; set; } = "08:00 - 16:00";
    public bool IsOnDuty { get; set; } = true;
}

public class DepartmentEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "fa-solid fa-stethoscope";
    public string Description { get; set; } = "";
    public bool IsEmergency247 { get; set; } = false;
}

public class MedicineEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public decimal SellingPrice { get; set; }
    public int StockQuantity { get; set; }
    public int MinStock { get; set; } = 15;
    public bool IsCosmetic { get; set; } = false;
}

public class LabTestEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public string SampleType { get; set; } = "Blood";
}

public class LabOrderEntity
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = $"LAB-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpperInvariant()}";
    public string PatientName { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Status { get; set; } = "Processing";
    public decimal Price { get; set; }
}

public class HomeNursingRequestEntity
{
    public int Id { get; set; }
    public string PatientName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string RequiredCare { get; set; } = "";
    public string Status { get; set; } = "Pending Dispatch";
}

public class InvoiceEntity
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}";
    public int PatientId { get; set; }
    public int InpatientDays { get; set; }
    public decimal DailyRate { get; set; }
    public decimal ConsultationFee { get; set; }
    public decimal PharmacyCharges { get; set; }
    public decimal LabCharges { get; set; }
    public decimal ServiceCharges { get; set; }
    public decimal Discount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UniversalHospitalDb : DbContext
{
    public UniversalHospitalDb(DbContextOptions<UniversalHospitalDb> options) : base(options) { }
    public DbSet<HospitalConfigEntity> HospitalConfigs => Set<HospitalConfigEntity>();
    public DbSet<PatientEntity> Patients => Set<PatientEntity>();
    public DbSet<DoctorEntity> Doctors => Set<DoctorEntity>();
    public DbSet<DepartmentEntity> Departments => Set<DepartmentEntity>();
    public DbSet<MedicineEntity> Medicines => Set<MedicineEntity>();
    public DbSet<LabTestEntity> LabTests => Set<LabTestEntity>();
    public DbSet<LabOrderEntity> LabOrders => Set<LabOrderEntity>();
    public DbSet<HomeNursingRequestEntity> HomeNursingRequests => Set<HomeNursingRequestEntity>();
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
}

public record CloneReq(string NewName, string NewCondition);
public record BillPayload(int NumberOfDays, decimal? DailyRate = null, bool IncludeConsultation = true, decimal PharmacyAmount = 0m, decimal LabAmount = 0m, decimal ServiceAmount = 0m, decimal Discount = 0m);
