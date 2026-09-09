# Development Rules
- ALWAYS check namespace before writing code (e.g., `backs2.Core` for logic, `backs2.ViewModels` for UI).
- ViewModels: Must be `public partial class` and inherit from `ObservableObject`.
- AXAML: Always ensure `x:Class="backs2.Views.YourView"`.
- File limits: Keep path depth shallow. 
- Style: Use High-Performance C# (minimal allocations, `List<T>` over LINQ in hot paths).