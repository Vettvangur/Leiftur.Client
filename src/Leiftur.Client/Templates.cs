namespace Leiftur.Client;

/// <summary>A stored message text with placeholders such as {{name}}.</summary>
/// <param name="Fields">The placeholder names used in the text.</param>
public sealed record TemplateDto(Guid Id, string Name, string Body, IReadOnlyList<string> Fields, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a template looks like with the given values, and what it would cost in segments.</summary>
/// <param name="Missing">Placeholders that got no value and have no fallback. A send with these would be refused.</param>
public sealed record TemplatePreview(string Body, SmsEncoding Encoding, int Length, int Segments, IReadOnlyList<string> Missing);

/// <summary>A named list of people to send to.</summary>
/// <param name="Contacts">How many contacts are on the list.</param>
public sealed record ContactListDto(Guid Id, string Name, int Contacts, DateTimeOffset CreatedAt);

/// <summary>A contact to put on a list.</summary>
public sealed record ContactInput
{
    public required string Number { get; init; }

    /// <summary>Values for template placeholders, e.g. name.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    public ContactInput()
    {
    }

    /// <summary>For example <c>new ContactInput("7801512", ("name", "Anna"))</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public ContactInput(string number, params (string Name, string Value)[] data)
    {
        Number = number;
        Data = data.Length == 0 ? null : data.ToDictionary(d => d.Name, d => d.Value);
    }
}

/// <param name="Added">Numbers that were new to the list.</param>
/// <param name="Updated">Numbers already on the list, whose data was replaced.</param>
/// <param name="Invalid">Inputs that are not valid phone numbers, or whose data is not acceptable. Nothing was stored for these.</param>
public sealed record AddContactsResponse(int Added, int Updated, IReadOnlyList<string> Invalid);

/// <param name="Number">Digits only, with country code, e.g. 3547801512.</param>
public sealed record ContactDto(string Number, string CountryCode, IReadOnlyDictionary<string, string> Data, DateTimeOffset CreatedAt);
