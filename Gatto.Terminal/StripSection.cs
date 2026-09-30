namespace Gatto.Terminal;

//where a section stands in the flow: pending, current or done, and done is a different fact from current
public enum StripState
{
    //not reached yet
    Pending,
    //the step the flow is on
    Current,
    //answered, shows its tick whether or not it is the focused section
    Done,
}

//one strip section as data: a flow test asserts the strip with no face and a render test asserts the row with no flow
public readonly record struct StripSection(string Name, StripState State);
