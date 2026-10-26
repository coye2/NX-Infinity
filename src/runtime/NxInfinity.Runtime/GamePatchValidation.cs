#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime;

public static class GamePatchValidation
{
    public const ulong MaximumModuleBytes = 512UL * 1024 * 1024;
    public const int MaximumWrites = 128, MaximumWriteBytes = 256, MaximumTotalBytes = 16384;

    public static GamePatchRecipe Canonicalize(GamePatchRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.SchemaVersion != 1 || !Token(recipe.RecipeId) || recipe.AdapterId != "module_bytes_v1" ||
            recipe.BackendId is not ("nxi") ||
            !Hex(recipe.BackendRevision, 40) || !Hex(recipe.TitleId, 16) || !Hex(recipe.BuildId, 64) ||
            recipe.DisplayVersion is not { Length: > 0 and <= 48 } ||
            !Regex.IsMatch(recipe.DisplayVersion, @"^[0-9]+(\.[0-9]+){0,3}([-+][A-Za-z0-9.-]{1,20})?$") ||
            recipe.TargetFrameRate is not (null or 60) || recipe.FrameGate is not ("original" or "swap_2_to_1") ||
            recipe.FrameGate == "swap_2_to_1" && recipe.TargetFrameRate != 60 || recipe.Writes is null ||
            recipe.Writes.Count is < 1 or > MaximumWrites)
            throw new NxException("invalid_game_patch", "A supported typed adapter and exact title/update/main-build/core identity are required.");
        int total = 0; ulong previousEnd = 0;
        var writes = new List<GamePatchWrite>();
        foreach (var write in recipe.Writes.OrderBy(write => write?.Offset))
        {
            if (write is null || write.Module != "main" || write.ExpectedHex is not { Length: >= 2 and <= MaximumWriteBytes * 2 } ||
                write.ExpectedHex.Length % 2 != 0 || !write.ExpectedHex.All(Uri.IsHexDigit) ||
                write.ReplacementHex is null || write.ReplacementHex.Length != write.ExpectedHex.Length ||
                !write.ReplacementHex.All(Uri.IsHexDigit) || write.ExpectedHex.Equals(write.ReplacementHex, StringComparison.OrdinalIgnoreCase))
                throw new NxException("invalid_game_patch", "Every bounded main-module write needs exact distinct original and replacement bytes.");
            int bytes = write.ExpectedHex.Length / 2;
            if (write.Offset > MaximumModuleBytes || (ulong)bytes > MaximumModuleBytes - write.Offset ||
                write.Offset < previousEnd || write.Offset % 4 != 0 || bytes % 4 != 0 || (total += bytes) > MaximumTotalBytes)
                throw new NxException("invalid_game_patch", "Executable writes must cover aligned whole instructions without overlaps and remain inside the bounded native module envelope.");
            previousEnd = write.Offset + (ulong)bytes;
            writes.Add(write with { ExpectedHex = write.ExpectedHex.ToUpperInvariant(), ReplacementHex = write.ReplacementHex.ToUpperInvariant() });
        }
        return recipe with { BackendRevision = recipe.BackendRevision.ToUpperInvariant(), TitleId = recipe.TitleId.ToUpperInvariant(),
            BuildId = recipe.BuildId.ToUpperInvariant(), Writes = writes.AsReadOnly() };
    }

    public static string Fingerprint(GamePatchRecipe recipe) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(Canonicalize(recipe), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));

    public static void ValidateAcknowledgement(GamePatchRequest request, GamePatchAcknowledgement ack, string expectedStatus)
    {
        var recipe = request.Recipe;
        if (!Guid.TryParseExact(request.TransactionId, "N", out _) || request.TransactionId != request.TransactionId.ToLowerInvariant() ||
            !Hex(request.RecipeSha256, 64) || expectedStatus is not ("prepared" or "applied" or "rolled_back"))
            throw new NxException("game_patch_acknowledgement_mismatch", "The patch acknowledgement request has an invalid identity or outcome.");
        if (ack is null || ack.SchemaVersion != 1 || ack.TransactionId != request.TransactionId ||
            !string.Equals(ack.RecipeSha256, request.RecipeSha256, StringComparison.OrdinalIgnoreCase) ||
            ack.RecipeId != recipe.RecipeId || ack.AdapterId != recipe.AdapterId || ack.BackendId != recipe.BackendId ||
            !string.Equals(ack.BackendRevision, recipe.BackendRevision, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ack.TitleId, recipe.TitleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ack.BuildId, recipe.BuildId, StringComparison.OrdinalIgnoreCase) || ack.DisplayVersion != recipe.DisplayVersion ||
            ack.Status != expectedStatus || ack.Boundary != "loaded_before_start" ||
            ack.TargetFrameRate != recipe.TargetFrameRate || ack.FrameGate != recipe.FrameGate ||
            ack.WriteCount != recipe.Writes.Count || ack.ByteCount != recipe.Writes.Sum(write => write.ExpectedHex.Length / 2) ||
            !ack.OriginalBytesVerified || !ack.ActiveModsVerified || ack.Generation <= 0 ||
            expectedStatus is "applied" or "rolled_back" && (!ack.ReadbackVerified || !ack.ExecutableInvalidationComplete) ||
            expectedStatus is "prepared" or "applied" && !ack.RollbackAvailable)
            throw new NxException("game_patch_acknowledgement_mismatch", "Native patch acknowledgement did not confirm the exact guarded transaction.");
    }

    private static bool Token(string? value) => value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit) && value.Any(c => c != '0');
}
