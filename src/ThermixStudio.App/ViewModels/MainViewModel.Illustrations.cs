using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    // Ilustrações persistidas em ProcessingJson do termograma selecionado
    public ObservableCollection<IIllustration> Illustrations { get; } = new();

    public async Task RemoveIllustrationByIdAsync(Guid id)
    {
        var existing = Illustrations.FirstOrDefault(i => i.Id == id);
        if (existing is null) return;
        PushIllustrationUndoSnapshot();
        Illustrations.Remove(existing);
        await PersistIllustrationsStateAsync();
    }

    public async Task AddIllustrationAsync(IIllustration illustration)
    {
        var normalized = illustration as ThermalIllustration ?? new ThermalIllustration
        {
            Id = illustration.Id,
            Type = illustration.Type,
            X1 = illustration.X1,
            Y1 = illustration.Y1,
            X2 = illustration.X2,
            Y2 = illustration.Y2,
            Text = illustration.Text
        };

        var existing = Illustrations.FirstOrDefault(i => i.Id == normalized.Id);
        if (existing is null)
        {
            PushIllustrationUndoSnapshot();
            Illustrations.Add(normalized);
        }
        else
        {
            var changed = existing.Type != normalized.Type || 
                          existing.X1 != normalized.X1 || existing.Y1 != normalized.Y1 || 
                          existing.X2 != normalized.X2 || existing.Y2 != normalized.Y2 || 
                          !string.Equals(existing.Text, normalized.Text, StringComparison.Ordinal);
            if (!changed) return;
            PushIllustrationUndoSnapshot();
            existing.Type = normalized.Type;
            existing.X1 = normalized.X1;
            existing.Y1 = normalized.Y1;
            existing.X2 = normalized.X2;
            existing.Y2 = normalized.Y2;
            existing.Text = normalized.Text;
        }
        await PersistIllustrationsStateAsync();
    }

    public async Task UpdateIllustrationAsync(Guid id, IIllustration illustration)
    {
        var existing = Illustrations.FirstOrDefault(i => i.Id == id);
        if (existing is null) return;
        var changed = existing.Type != illustration.Type || 
                      existing.X1 != illustration.X1 || existing.Y1 != illustration.Y1 || 
                      existing.X2 != illustration.X2 || existing.Y2 != illustration.Y2 || 
                      !string.Equals(existing.Text, illustration.Text, StringComparison.Ordinal);
        if (!changed) return;
        PushIllustrationUndoSnapshot();
        existing.Type = illustration.Type;
        existing.X1 = illustration.X1;
        existing.Y1 = illustration.Y1;
        existing.X2 = illustration.X2;
        existing.Y2 = illustration.Y2;
        existing.Text = illustration.Text;
        await PersistIllustrationsStateAsync();
    }

    private async Task PersistIllustrationsStateAsync()
    {
        if (SelectedThermogram is null) return;
        PersistCurrentStateToSelectedThermogram();
        await _dataService.UpdateThermogramAsync(SelectedThermogram);
    }

    private void PushIllustrationUndoSnapshot()
    {
        if (_isRestoringIllustrationUndo || SelectedThermogram is null) return;
        var stack = GetIllustrationUndoStack(SelectedThermogram.Id);
        var snapshot = Illustrations.OfType<ThermalIllustration>().Select(CloneIllustration).ToList();
        stack.Push(snapshot);
        const int maxUndoSteps = 50;
        if (stack.Count <= maxUndoSteps) return;
        var trimmed = stack.Take(maxUndoSteps).Reverse().ToArray();
        stack.Clear();
        foreach (var state in trimmed) stack.Push(state);
    }

    private Stack<List<ThermalIllustration>> GetIllustrationUndoStack(Guid thermogramId)
    {
        if (_illustrationUndoHistory.TryGetValue(thermogramId, out var existing)) return existing;
        var created = new Stack<List<ThermalIllustration>>();
        _illustrationUndoHistory[thermogramId] = created;
        return created;
    }

    private async Task<bool> TryUndoIllustrationActionAsync()
    {
        if (SelectedThermogram is null) return false;
        var stack = GetIllustrationUndoStack(SelectedThermogram.Id);
        if (stack.Count == 0) return false;
        var previous = stack.Pop();
        _isRestoringIllustrationUndo = true;
        try
        {
            Illustrations.Clear();
            foreach (var item in previous) Illustrations.Add(CloneIllustration(item));
            await PersistIllustrationsStateAsync();
            StatusMessage = "Ultima ilustração desfeita (Ctrl+Z).";
            return true;
        }
        finally { _isRestoringIllustrationUndo = false; }
    }

    private static ThermalIllustration CloneIllustration(ThermalIllustration source) => new() { Id = source.Id, Type = source.Type, X1 = source.X1, Y1 = source.Y1, X2 = source.X2, Y2 = source.Y2, Text = source.Text };
}
