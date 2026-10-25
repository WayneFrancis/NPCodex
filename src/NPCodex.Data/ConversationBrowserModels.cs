namespace NPCodex.Data;

/// <summary>One approved utterance and the characters who could hear it.</summary>
public sealed record StatementInfo(
    long Id, int? WorldDay, TimeOnly? WorldTime,
    string Speaker, string Hearers, string Content, double Relevance);

/// <summary>Compact D&D 5e-inspired sheet. All data is DM-entered.</summary>
public sealed record DndCharacterSheet(
    string Species = "", string CharacterClass = "", int Level = 1,
    string Background = "", string Alignment = "",
    short Strength = 10, short Dexterity = 10, short Constitution = 10,
    short Intelligence = 10, short Wisdom = 10, short Charisma = 10,
    int ArmorClass = 10, int HitPoints = 1, int SpeedFeet = 30,
    string Features = "", string Equipment = "", string Notes = "");
