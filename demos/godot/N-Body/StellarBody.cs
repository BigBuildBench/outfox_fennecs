using System;
using System.Linq;
using Godot;

namespace fennecs.demos.godot;

[GlobalClass]
public partial class StellarBody : EntityNode2D
{
	public float mass = 1.0f;

	private Body _body;

	public override void _EnterTree()
	{
		base._EnterTree();

		if (!entity.Alive) return;

		mass = Random.Shared.NextSingle() * 0.5f + 0.75f;
		Scale *= mass*mass;

		entity.Add(this);

		var position = new Position { Value = new(Position.X, Position.Y) };
		entity.Add(position);

		_body = new() { position = position.Value, mass = mass };
		entity.Add(_body);

		entity.Add(new Velocity { Value = new((Random.Shared.NextSingle()-0.5f) * 100f, (Random.Shared.NextSingle()-0.5f) * 100f) });
		entity.Add(new Acceleration { Value = new(0, 0) });
	}

	public override void _Ready()
	{
		base._Ready();
		if (!entity.Alive) return;

		// Get all sibling bodies (these were all set up in _EnterTree)
		// (we need to check if they got an entity assigned because of the
		// slightly hacky spawn chance mechanic, my bad. :o)
		var siblings = GetParent()
			.GetChildren()
			.OfType<StellarBody>()
			.Where(body => body.entity.Alive);

		// Add all attractor relations to our own entity;
		// we include ourselves - this means that all entities in
		// this star system will end up in the same Archetype.
		// It's not strictly necessary, but improves cache coherence at
		// the cost of a single reference compare and 1 additional stored
		// pointer per entity.
		foreach (var sibling in siblings)
		{
			if (sibling.entity.Alive)
			{
				entity.Add(sibling._body, sibling.entity);
			}
		}
	}
}
