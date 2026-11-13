using System.Threading.Tasks;

namespace backs2.Services;

public interface IDialogService
{
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
    string[]? OpenFileDialog(string filter, string title);
    string? SaveFileDialog(string filter, string title);
    bool ShowModal(object viewModel);
}
