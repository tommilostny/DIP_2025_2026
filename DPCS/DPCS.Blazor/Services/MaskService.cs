using System.Text.Json;
using System.Text.RegularExpressions;

namespace DPCS.Blazor.Services;

/// <summary>
/// Service for managing and validating Hashcat masks.
/// </summary>
public partial class MaskService
{
    private readonly string _masksDirectory;
    private readonly string _masksFilePath;
    private readonly ILogger<MaskService> _logger;
    private const int MaxSavedMasks = 20;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"(\?[ludhsabH?1234])|([^?])", RegexOptions.None, matchTimeoutMilliseconds: 66666)]
    private static partial Regex MaskValidationRegex();

    public MaskService(IWebHostEnvironment env, ILogger<MaskService> logger)
    {
        _masksDirectory = Path.Combine(env.ContentRootPath, "data", "masks");
        _masksFilePath = Path.Combine(_masksDirectory, "masks.json");
        _logger = logger;
        Directory.CreateDirectory(_masksDirectory);
    }

    /// <summary>
    /// Gets the list of unique, saved masks.
    /// </summary>
    public IReadOnlyList<string> GetSavedMasks()
    {
        if (!File.Exists(_masksFilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_masksFilePath);
            var masks = JsonSerializer.Deserialize<List<string>>(json);
            var maskList = masks ?? [];
            maskList.Reverse(); // Show most recent first
            return maskList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading saved masks from {FilePath}", _masksFilePath);
            return [];
        }
    }

    /// <summary>
    /// Saves a collection of masks, merging them with existing saved masks.
    /// </summary>
    public void SaveMasks(IReadOnlyCollection<string> newMasks)
    {
        try
        {
            var existingMasksList = GetSavedMasks().Reverse().ToList(); // Get in original order
            var existingMasksSet = existingMasksList.ToHashSet();
            var hasChanges = false;

            foreach (var mask in newMasks)
            {
                if (existingMasksSet.Add(mask))
                {
                    existingMasksList.Add(mask);
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                // Enforce the limit
                if (existingMasksList.Count > MaxSavedMasks)
                {
                    existingMasksList.RemoveRange(0, existingMasksList.Count - MaxSavedMasks);
                }

                var json = JsonSerializer.Serialize(existingMasksList, _jsonOptions);
                File.WriteAllText(_masksFilePath, json);
                _logger.LogInformation("Saved masks to {FilePath}", _masksFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving masks to {FilePath}", _masksFilePath);
        }
    }

    /// <summary>
    /// Deletes a specific mask from the saved masks file.
    /// </summary>
    /// <param name="maskToDelete">The mask to delete.</param>
    public void DeleteMask(string maskToDelete)
    {
        if (string.IsNullOrWhiteSpace(maskToDelete)) return;

        try
        {
            var existingMasks = GetSavedMasks().ToHashSet();
            if (existingMasks.Remove(maskToDelete))
            {
                var json = JsonSerializer.Serialize(existingMasks, _jsonOptions);
                File.WriteAllText(_masksFilePath, json);
                _logger.LogInformation("Deleted mask '{Mask}' from {FilePath}", maskToDelete, _masksFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting mask from {FilePath}", _masksFilePath);
            throw; // Re-throw to notify the UI of the failure
        }
    }

    /// <summary>
    /// Validates a mask against built-in and provided custom charsets.
    /// </summary>
    /// <param name="mask">The mask string to validate.</param>
    /// <param name="customCharsets">A collection of the custom charsets (?1, ?2, etc.) that are defined for this job.</param>
    /// <returns>True if the mask is valid, otherwise false.</returns>
    public bool IsMaskValid(string mask, IReadOnlyCollection<string> customCharsets)
    {
        if (string.IsNullOrWhiteSpace(mask)) return false;

        var matches = MaskValidationRegex().Matches(mask);
        
        // The sum of the lengths of all matches must equal the original mask length.
        // If not, it means there was an invalid character sequence (e.g., `?x`).
        if (matches.Sum(m => m.Length) != mask.Length) return false;

        // Check if any custom placeholders are used without being defined.
        if (mask.Contains("?1") && string.IsNullOrEmpty(customCharsets.ElementAtOrDefault(0))) return false;
        if (mask.Contains("?2") && string.IsNullOrEmpty(customCharsets.ElementAtOrDefault(1))) return false;
        if (mask.Contains("?3") && string.IsNullOrEmpty(customCharsets.ElementAtOrDefault(2))) return false;
        if (mask.Contains("?4") && string.IsNullOrEmpty(customCharsets.ElementAtOrDefault(3))) return false;

        return true;
    }
}