using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class RefactorTools
{
    private const int MaxChangedDocuments = 100;

    [McpServerTool]
    [Description("PREVIEW a solution-wide rename of the symbol at a position: shows every file and line that would change, without modifying anything. Apply the changes with your file-editing tools if the preview looks right.")]
    public static Task<string> RenameSymbolPreview(
        [Description("Absolute path of the file containing the symbol")] string filePath,
        [Description("1-based line number of the symbol")] int line,
        [Description("1-based column of the symbol")] int column,
        [Description("The new name for the symbol")] string newName)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await NavigationTools.ResolveSymbolAsync(filePath, line, column);
            var solution = WorkspaceHost.Current;

            var renamed = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName);

            var changes = new List<object>();
            foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
            {
                foreach (var docId in projectChange.GetChangedDocuments())
                {
                    if (changes.Count >= MaxChangedDocuments) break;

                    var oldDoc = solution.GetDocument(docId)!;
                    var newDoc = renamed.GetDocument(docId)!;
                    var oldText = await oldDoc.GetTextAsync();
                    var textChanges = await newDoc.GetTextChangesAsync(oldDoc);

                    changes.Add(new
                    {
                        file = oldDoc.FilePath,
                        edits = textChanges.Select(tc => new
                        {
                            line = oldText.Lines.GetLinePosition(tc.Span.Start).Line + 1,
                            oldText = oldText.ToString(tc.Span),
                            newText = tc.NewText
                        }).ToList()
                    });
                }
            }

            return new
            {
                symbol = NavigationTools.Describe(symbol),
                newName,
                changedFileCount = changes.Count,
                note = "Preview only - no files were modified.",
                changes
            };
        });
    }
}
