namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru /CAM/Patients — listă pacienți + search.
    /// </summary>
    public class CamPatientsListViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public List<Row> Items { get; set; } = new();

        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public int AnalysesCount { get; set; }
        }
    }
}
