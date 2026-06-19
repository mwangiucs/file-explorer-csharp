using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FileExplorerCS;

public static class LicenseService
{
    private const string Salt = "FileExplorerCS-Secret-Salt-2026";
    private const long XorKey = 0x7A2F6D19B4C83E5D;
    private const string RegistrySubKey = @"Software\FileExplorerCS";
    private const string InstallValName = "InstallVal";
    private const string ActiveValName = "ActiveVal";

    public static bool IsRegistered { get; private set; }
    public static string RegisteredEmail { get; private set; } = string.Empty;
    public static string RegisteredKey { get; private set; } = string.Empty;
    public static int DaysRemaining { get; private set; }
    public static bool IsTrialExpired { get; private set; }
    public static bool IsClockRollbackDetected { get; private set; }

    public static void Initialize()
    {
        // 1. Load configuration settings
        var settings = AppSettingsStore.Load();
        string savedEmail = settings.LicenseEmail;
        string savedKey = settings.LicenseKey;

        // 2. Validate saved license key
        if (Validate(savedEmail, savedKey))
        {
            IsRegistered = true;
            RegisteredEmail = savedEmail;
            RegisteredKey = savedKey;
            return;
        }

        // 3. Not registered - run trial verification
        IsRegistered = false;
        DateTime today = DateTime.Today;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey);
            if (key == null)
            {
                // Fallback if registry is unavailable: run as expired or fresh
                IsTrialExpired = true;
                return;
            }

            string? installToken = key.GetValue(InstallValName) as string;
            string? activeToken = key.GetValue(ActiveValName) as string;

            DateTime startDate;
            DateTime lastActiveDate;

            if (string.IsNullOrEmpty(installToken))
            {
                // First launch! Write starting date and last active date.
                startDate = today;
                lastActiveDate = today;

                key.SetValue(InstallValName, ObfuscateDate(startDate));
                key.SetValue(ActiveValName, ObfuscateDate(lastActiveDate));
            }
            else
            {
                startDate = DeobfuscateDate(installToken);
                lastActiveDate = string.IsNullOrEmpty(activeToken) ? today : DeobfuscateDate(activeToken);
            }

            // Check clock rollback
            if (today < lastActiveDate.Date)
            {
                IsClockRollbackDetected = true;
                IsTrialExpired = true;
                DaysRemaining = 0;
                return;
            }

            // Update last active date to today
            if (today > lastActiveDate)
            {
                key.SetValue(ActiveValName, ObfuscateDate(today));
            }

            // Calculate remaining trial days
            int daysUsed = (today - startDate.Date).Days;
            if (daysUsed < 0)
            {
                // This means the clock was rolled back prior to installation time
                IsClockRollbackDetected = true;
                IsTrialExpired = true;
                DaysRemaining = 0;
            }
            else if (daysUsed >= 14)
            {
                IsTrialExpired = true;
                DaysRemaining = 0;
            }
            else
            {
                IsTrialExpired = false;
                DaysRemaining = 14 - daysUsed;
            }
        }
        catch
        {
            // Fail secure: if we can't write registry or check it, trigger trial expired
            IsTrialExpired = true;
            DaysRemaining = 0;
        }
    }

    public static bool Register(string email, string key)
    {
        if (Validate(email, key))
        {
            IsRegistered = true;
            RegisteredEmail = email;
            RegisteredKey = key;

            // Save settings
            var settings = AppSettingsStore.Load();
            settings.LicenseEmail = email;
            settings.LicenseKey = key;
            AppSettingsStore.Save(settings);

            return true;
        }
        return false;
    }

    public static bool Validate(string email, string key)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string expectedKey = GenerateKey(email);
        return string.Equals(key.Trim(), expectedKey, StringComparison.OrdinalIgnoreCase);
    }

    public static string GenerateKey(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Salt));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        string hex = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
        return $"{hex[..5]}-{hex[5..10]}-{hex[10..15]}-{hex[15..20]}";
    }

    private static string ObfuscateDate(DateTime date)
    {
        long obfuscatedTicks = date.Ticks ^ XorKey;
        return Convert.ToBase64String(BitConverter.GetBytes(obfuscatedTicks));
    }

    private static DateTime DeobfuscateDate(string token)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            long obfuscatedTicks = BitConverter.ToInt64(bytes, 0);
            long ticks = obfuscatedTicks ^ XorKey;
            return new DateTime(ticks);
        }
        catch
        {
            // If corrupt token, return ancient date so trial shows expired
            return DateTime.MinValue;
        }
    }
}
