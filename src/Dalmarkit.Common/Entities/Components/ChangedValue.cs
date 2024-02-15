namespace Dalmarkit.Common.Entities.Components;

public class ChangedValue(object oldValue, object newValue)
{
    public object OldValue { get; set; } = oldValue;

    public object NewValue { get; set; } = newValue;
}
