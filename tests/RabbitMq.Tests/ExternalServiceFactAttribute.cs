namespace RabbitMq.Tests;

// These tests can create accounts, bookings and messages. Require an explicit test target.
public sealed class ExternalServiceFactAttribute : FactAttribute
{
    public ExternalServiceFactAttribute(bool requiresAdmin = false, bool rabbitMq = false)
    {
        if (Environment.GetEnvironmentVariable("TOURBOOKING_EXTERNAL_TESTS") != "1")
            Skip = "Set TOURBOOKING_EXTERNAL_TESTS=1 to run against isolated external services.";
        else if (!rabbitMq && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOURBOOKING_URL")))
            Skip = "Requires an explicit TOURBOOKING_URL pointing to an isolated test stack.";
        else if (requiresAdmin && (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_ADMIN_EMAIL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_ADMIN_PASSWORD"))))
            Skip = "Requires TEST_ADMIN_EMAIL and TEST_ADMIN_PASSWORD for the test stack.";
    }
}
