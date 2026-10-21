namespace Retroscope.Core.Ids;

public interface IGuidId<TSelf> where TSelf : struct, IGuidId<TSelf> {
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}
