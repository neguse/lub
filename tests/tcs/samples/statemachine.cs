public enum DoorState
{
    Closed = 0,
    Open = 1,
    Locked = 2
}

public enum DoorCommand
{
    Open = 0,
    Close = 1,
    Lock = 2,
    Unlock = 3,
    Reset = 4
}

public class Door
{
    public DoorState State;

    public Door(DoorState initial)
    {
        State = initial;
    }

    public void Apply(DoorCommand command)
    {
        switch (command)
        {
            default:
                State = DoorState.Closed;
                break;
            case DoorCommand.Open:
                if (State != DoorState.Locked)
                {
                    State = DoorState.Open;
                }
                break;
            case DoorCommand.Close:
                State = DoorState.Closed;
                break;
            case DoorCommand.Lock:
                if (State == DoorState.Closed)
                {
                    State = DoorState.Locked;
                }
                break;
            case DoorCommand.Unlock:
                if (State == DoorState.Locked)
                {
                    State = DoorState.Closed;
                }
                break;
        }
    }

    public string Label()
    {
        return State switch
        {
            DoorState.Closed => "closed",
            DoorState.Open => "open",
            DoorState.Locked => "locked",
            _ => "unknown"
        };
    }
}

public class StateMachineSample
{
    public static string Run()
    {
        var door = new Door(DoorState.Closed);
        door.Apply(DoorCommand.Open);
        var a = door.Label();
        door.Apply(DoorCommand.Lock);
        var b = door.Label();
        door.Apply(DoorCommand.Close);
        door.Apply(DoorCommand.Lock);
        var c = door.Label();
        door.Apply(DoorCommand.Reset);
        var d = door.Label();
        return $"{a},{b},{c},{d}";
    }
}
