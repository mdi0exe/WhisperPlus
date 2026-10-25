namespace WhisperPlus.Models;

public sealed record AudioDeviceItem(int Index, string Name, bool IsDefault)
{
	public override string ToString()
	{
		if (!IsDefault)
		{
			return Name;
		}
		return Name + " (Default)";
	}
}
