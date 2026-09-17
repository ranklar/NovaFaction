using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class CommandTests
{
    private static readonly FixVector2 Somewhere = new FixVector2(Fix.FromInt(3), Fix.Parse("-1.5"));

    [Fact]
    public void Factories_SetFields()
    {
        Command deploy = Command.DeployCard(7, 1, 2, 3, Somewhere);
        Assert.Equal(7, deploy.Tick);
        Assert.Equal(1, deploy.Player);
        Assert.Equal(2, deploy.Sequence);
        Assert.Equal(CommandType.DeployCard, deploy.Type);
        Assert.Equal(3, deploy.HandSlot);
        Assert.Equal(Somewhere, deploy.Target);

        Command ability = Command.LeaderAbility(1, 0, 0, Somewhere);
        Assert.Equal(Command.NoHandSlot, ability.HandSlot);

        Command none = Command.None(1, 0, 0);
        Assert.Equal(CommandType.None, none.Type);
        Assert.Equal(FixVector2.Zero, none.Target);
    }

    [Fact]
    public void WellFormedCommands_Validate()
    {
        Assert.Null(Command.None(0, 0, 0).Validate(4));
        Assert.Null(Command.DeployCard(0, 1, 5, 0, Somewhere).Validate(4));
        Assert.Null(Command.DeployCard(0, 1, 5, 3, Somewhere).Validate(4));
        Assert.Null(Command.LeaderAbility(0, 0, 0, Somewhere).Validate(4));
    }

    public static IEnumerable<object[]> MalformedCommands()
    {
        var target = new FixVector2(Fix.One, Fix.One);
        yield return new object[] { new Command(-1, 0, 0, CommandType.None, -1, FixVector2.Zero), "tick" };
        yield return new object[] { new Command(0, 2, 0, CommandType.None, -1, FixVector2.Zero), "player" };
        yield return new object[] { new Command(0, -1, 0, CommandType.None, -1, FixVector2.Zero), "player" };
        yield return new object[] { new Command(0, 0, -1, CommandType.None, -1, FixVector2.Zero), "sequence" };
        yield return new object[] { new Command(0, 0, 0, (CommandType)99, -1, FixVector2.Zero), "unknown command type" };
        yield return new object[] { new Command(0, 0, 0, CommandType.None, 0, FixVector2.Zero), "None" };
        yield return new object[] { new Command(0, 0, 0, CommandType.None, -1, target), "None" };
        yield return new object[] { Command.DeployCard(0, 0, 0, -1, target), "hand slot" };
        yield return new object[] { Command.DeployCard(0, 0, 0, 4, target), "hand slot" };
        yield return new object[] { new Command(0, 0, 0, CommandType.LeaderAbility, 0, target), "LeaderAbility" };
    }

    [Theory]
    [MemberData(nameof(MalformedCommands))]
    public void MalformedCommands_AreReported(Command command, string fragment)
    {
        string? problem = command.Validate(handSize: 4);
        Assert.NotNull(problem);
        Assert.Contains(fragment, problem);
    }

    [Fact]
    public void CanonicalOrder_IsTickThenPlayerThenSequence()
    {
        var input = new List<Command>
        {
            Command.None(2, 0, 0),
            Command.None(1, 1, 0),
            Command.None(1, 0, 9),
            Command.None(1, 0, 3),
            Command.None(0, 1, 1),
            Command.None(0, 1, 0),
        };
        Command[] sorted = Command.SortCanonical(input);

        Assert.Equal(new[]
        {
            Command.None(0, 1, 0),
            Command.None(0, 1, 1),
            Command.None(1, 0, 3),
            Command.None(1, 0, 9),
            Command.None(1, 1, 0),
            Command.None(2, 0, 0),
        }, sorted);
        Assert.Equal(6, input.Count); // input untouched
        Assert.Equal(Command.None(2, 0, 0), input[0]);
    }

    [Fact]
    public void CanonicalOrder_DoesNotDependOnInputOrder()
    {
        var rng = new SimRandom(5);
        var commands = new List<Command>();
        for (int i = 0; i < 40; i++)
        {
            commands.Add(Command.DeployCard(rng.NextInt(0, 3), rng.NextInt(0, 2), i, rng.NextInt(0, 4), Somewhere));
        }
        Command[] expected = Command.SortCanonical(commands);
        for (int round = 0; round < 20; round++)
        {
            rng.Shuffle(commands);
            Assert.Equal(expected, Command.SortCanonical(commands));
        }
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        Command a = Command.DeployCard(1, 0, 0, 2, Somewhere);
        Assert.Equal(a, Command.DeployCard(1, 0, 0, 2, Somewhere));
        Assert.True(a == Command.DeployCard(1, 0, 0, 2, Somewhere));
        Assert.NotEqual(a, Command.DeployCard(1, 0, 0, 1, Somewhere));
        Assert.NotEqual(a, Command.DeployCard(1, 0, 0, 2, FixVector2.Zero));
        Assert.True(a != Command.LeaderAbility(1, 0, 0, Somewhere));
    }
}

public class CommandLogTests
{
    [Fact]
    public void RecordsTicksInOrderAndStoresCanonicalOrder()
    {
        var log = new CommandLog();
        log.Record(0, Array.Empty<Command>());
        log.Record(1, new[] { Command.None(1, 1, 0), Command.None(1, 0, 1), Command.None(1, 0, 0) });

        Assert.Equal(2, log.TickCount);
        Assert.Equal(3, log.CommandCount);
        Assert.Empty(log.GetCommands(0));
        Assert.Equal(new[] { Command.None(1, 0, 0), Command.None(1, 0, 1), Command.None(1, 1, 0) }, log.GetCommands(1));
    }

    [Fact]
    public void RejectsSkippedOrRepeatedTicks()
    {
        var log = new CommandLog();
        Assert.Throws<ArgumentException>(() => log.Record(1, Array.Empty<Command>()));
        log.Record(0, Array.Empty<Command>());
        Assert.Throws<ArgumentException>(() => log.Record(0, Array.Empty<Command>()));
        Assert.Equal(1, log.TickCount);
    }

    [Fact]
    public void RejectsCommandStampedForAnotherTick()
    {
        var log = new CommandLog();
        var ex = Assert.Throws<ArgumentException>(() => log.Record(0, new[] { Command.None(1, 0, 0) }));
        Assert.Contains("submitted on tick 0", ex.Message);
        Assert.Equal(0, log.TickCount);
    }

    [Fact]
    public void RejectsDuplicatePlayerSequence()
    {
        var log = new CommandLog();
        var ex = Assert.Throws<ArgumentException>(() =>
            log.Record(0, new[] { Command.None(0, 0, 4), Command.None(0, 1, 4), Command.None(0, 0, 4) }));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Equal(0, log.TickCount);
    }

    [Fact]
    public void GetCommands_OutOfRange_Throws()
    {
        var log = new CommandLog();
        Assert.Throws<ArgumentOutOfRangeException>(() => log.GetCommands(0));
        Assert.Throws<ArgumentNullException>(() => log.Record(0, null!));
    }
}
