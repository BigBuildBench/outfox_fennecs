﻿// SPDX-License-Identifier: MIT

using System;
using fennecs;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Events;
using Environment = System.Environment;

namespace Cubes;

/// <summary>
///     <para>
///         DemoCubes (Stride version)
///     </para>
///     <para>
///         All motion  is 100% CPU simulation (no GPU). Here, we demonstrate a simple case how to update the positions of a large number of Entities.
///     </para>
///     <para>
///         State is stored in Components on the Entities:
///     </para>
///     <ul>
///         <li>1x System.Numerics.Vector3 (as Position)</li>
///         <li>1x Matrix4x3 (custom struct, as Transform)</li>
///         <li>1x integer (as a simple identifier)</li>
///     </ul>
///     <para>
///         The state is transferred into the Stride Engine in bulk each frame using Query.Raw and submitting just the Matrix4x3 structs directly to a MultiMesh.
///     </para>
///     <para>
///         That static buffer is then used by Stride's Renderer to display the Cubes.
///     </para>
/// </summary>
public class CubeDemo : SyncScript
{
    //  Config: Maximum # of Entities that can be spawned. For brevity, made const instead
    // of [Export] so we don't have to pass it in as an additional uniform.
    private const int MaxEntities = 313370;

    //  Calculation: Internal Speed of the Simulation.
    private const float BaseTimeScale = 0.0005f;

    //  Fennecs: The World that will contain the Entities.
    private readonly World _world = new();

    //  Calculation: Visible CubeCount (can be smoothed to be passed in as an uniform).
    private float _cubeCount = 1;
    private Vector3 _currentAmplitude;

    //  Calculation: Smoothed values expressing the portion of Entities that are visible.
    private float _currentRenderedFraction;
    private float _currentTimeScale = BaseTimeScale;

    //  Calculation: Smoothed values for the simulation.
    private Vector3 _goalAmplitude;

    //  Fennecs: The Query that will be used to interact with the Entities.
    private Query<Matrix, Vector3, int> _query;

    // ?? Boilerplate: Array used to copy the Entity Transform data into Stride's MultiMesh.
    private Matrix[] _submissionArray = Array.Empty<Matrix>();


    //  Calculation: Elapsed time value for the simulation.
    private float _time;

    //  Stride: The main MultiMeshInstance3D that will be used to render the cubes.
    public InstancingUserArray InstancingArray;

    public float MaxAmplitude = 400;
    public float MinAmplitude = 250;

    //  Stride: Exports to interact with the UI
    public Slider RenderedSlider;
    public Slider SimulatedSlider;
    public TextBlock EntityCountText;

    //  Stride: Read by the UI to show the simulated Entity count. (not just the visible ones)
    private int QueryCount => _query.Count;


    /// <summary>
    ///     Spawn or Remove Entities to match the desired count.
    /// </summary>
    /// <param name="spawnCount">the count of entities to simulate</param>
    private void SetEntityCount(int spawnCount)
    {
        // Spawn new entities if needed.
        for (var i = _query.Count; i < spawnCount; i++)
            _world.Spawn().Add(i)
                .Add<Matrix>()
                .Add<Vector3>();

        // Cut off excess entities, if any.
        _query.Truncate(spawnCount);

        EntityCountText.Text = $"Entities: {spawnCount}\nVisible: {_cubeCount}";
    }


    /// <summary>
    ///     Stride Start() method, sets up our simulation.
    /// </summary>
    public override void Start()
    {
        var component = Entity.Get<InstancingComponent>();
        InstancingArray = (InstancingUserArray) component.Type;

        Array.Resize(ref _submissionArray, MaxEntities);

        var root = Entity.Get<UIComponent>().Page.RootElement;
        
        var infoBlock = root.FindVisualChildOfType<TextBlock>("InfoText");
        infoBlock.Text = InfoText;
        
        
        SimulatedSlider = root.FindVisualChildOfType<Slider>("SimulatedSlider");
        RenderedSlider = root.FindVisualChildOfType<Slider>("RenderedSlider");

        EntityCountText = root.FindVisualChildOfType<TextBlock>("EntityCountText");

        SimulatedSlider.ValueChanged += _on_simulated_slider_value_changed;
        RenderedSlider.ValueChanged += _on_rendered_slider_value_changed;

        //  Boilerplate: Prepare our Query that we'll use to interact with the Entities.
        _query = _world.Query<Matrix, Vector3, int>().Build();

        //  Boilerplate: Users can change the number of entities, so pre-warm the memory allocator a bit.
        SetEntityCount(MaxEntities);

        //  Boilerplate: Apply the initial state of the UI.
        _on_simulated_slider_value_changed(SimulatedSlider, routed_event_args: null);
        _on_rendered_slider_value_changed(RenderedSlider, routed_event_args: null);
    }


    /// <summary>
    ///     Stride Update() method, updates the simulation and passes data to Stride.
    /// </summary>
    public override void Update()
    {
        //  Calculation: Convert the delta time to a float (preferred use here).
        var dt = (float) Game.UpdateTime.Elapsed.TotalSeconds;

        //  Calculation: Accumulate the total elapsed time by adding the current frame time.
        _time += dt * _currentTimeScale;

        //  Calculation: Determine the number of entities that will be displayed (also used to smooth out animation).

        // -----------------------  HERE'S WHERE THE SIMULATION WORK IS RUN ------------------------
        var chunkSize = Math.Max(_query.Count / Environment.ProcessorCount, val2: 128);
        _query.Job(UpdatePositionForCube, (_time, _currentAmplitude, _cubeCount, dt), chunkSize);

        //  Make the cloud of cubes denser if there are more cubes.
        var amplitudePortion = Math.Clamp(1.0f - _query.Count / (float) MaxEntities, min: 0f, max: 1f);
        _goalAmplitude = MathUtil.Lerp(MinAmplitude, MaxAmplitude, amplitudePortion) * Vector3.One;
        _currentAmplitude = _currentAmplitude * 0.9f + 0.1f * _goalAmplitude;

        // ------------------------  HERE IS WHERE THE DATA IS SENT TO Stride ------------------------
        //  Copy transforms into Instanced Mesh
        _query.Raw(static delegate(Memory<Matrix> transforms, (InstancingUserArray instancingArray, Matrix[] submissionArray, int cubeCount) uniform)
        {
            transforms.CopyTo(uniform.submissionArray);
            uniform.instancingArray.UpdateWorldMatrices(uniform.submissionArray, uniform.cubeCount);
        }, (InstancingArray, _submissionArray, (int) _cubeCount));
    }


    // -----------------------  HERE'S WHERE THE SIMULATION WORK IS RUN ------------------------
    //  Update Transforms and Positions of all Cube Entities.
    // -------------------------------------------------------------------------------------------
    private static void UpdatePositionForCube(
        ref Matrix transform,
        ref Vector3 position,
        ref int index,
        (float Time, Vector3 Amplitude, float CubeCount, float dt) uniform)
    {
        #region Motion Calculations (just generic math for the cube motion)
        //  Calculation: Apply a chaotic Lissajous-like motion for the cubes
        var motionIndex = (index + uniform.Time * MathF.Tau * 69f) % uniform.CubeCount - uniform.CubeCount / 2f;

        var entityRatio = uniform.CubeCount / MaxEntities;

        var phase1 = motionIndex * MathF.Sin(motionIndex / 1500f * MathF.Tau) * 7f * MathF.Tau / uniform.CubeCount;
        var phase2 = motionIndex * MathF.Sin(motionIndex / 1700f * MathF.Tau) * (MathF.Sin(uniform.Time * 23f) + 1.5f) * 5f * MathF.Tau / uniform.CubeCount;
        var phase3 = motionIndex * MathF.Sin(motionIndex / 1000f * MathF.Tau) * (MathF.Sin(uniform.Time * 13f) + 1.5f) * 11f * entityRatio * MathF.Tau / uniform.CubeCount;

        var vector = new Vector3
        {
            X = MathF.Sin(phase1 + uniform.Time * 2f + motionIndex / 1500f),
            Y = MathF.Sin(phase2 + uniform.Time * 3f + motionIndex / 1000f),
            Z = MathF.Sin(phase3 + uniform.Time * 5f + motionIndex / 2000f),
        };


        var cubic = MathF.Sin(uniform.Time * 100f * MathF.Tau) * 0.5f + 0.5f;
        var shell = Math.Clamp(vector.Length(), min: 0, max: 1);
        vector = (1.0f - cubic) * shell * vector / vector.Length() + cubic * vector;
        #endregion


        //  Update Component: Store position state, smoothing it to illustrate accumulative operations using data from the past frame.
        position = Fir(position, vector, k: 0.99f, uniform.dt);

        //  Update Component: Build & store Matrix Transform (for the MultiMesh), scaling sizes between 1 and 3
        var scale = 2f * (1.5f - MathF.Sqrt(uniform.CubeCount / MaxEntities));

        var goodMatrix = Matrix.Scaling(scale * Vector3.One) * Matrix.Translation(position * uniform.Amplitude);
        transform = goodMatrix;
    }


    #region Signal Handlers
    private void _on_rendered_slider_value_changed(object sender, RoutedEventArgs routed_event_args)
    {
        var slider = (Slider) sender;
        var value = slider.Value;

        // Set the number of entities to render
        _currentRenderedFraction = value;

        // Move cubes faster if there are fewer visible
        _currentTimeScale = BaseTimeScale / MathF.Max(value, y: 0.3f);
        
        _cubeCount = (int) Math.Floor(_currentRenderedFraction * _query.Count);
        EntityCountText.Text = $"Entities: {QueryCount}\nVisible: {_cubeCount}";
    }


    private void _on_simulated_slider_value_changed(object sender, RoutedEventArgs routed_event_args)
    {
        var slider = (Slider) sender;
        var value = slider.Value;
        // Set the number of entities to simulate
        var count = (int) Math.Ceiling(Math.Pow(value, MathF.Sqrt(x: 2)) * MaxEntities);
        count = Math.Clamp((count / 100 + 1) * 100, min: 0, MaxEntities);
        SetEntityCount(count);

        _cubeCount = (int) Math.Floor(_currentRenderedFraction * _query.Count);
        EntityCountText.Text = $"Entities: {QueryCount}\nVisible: {_cubeCount}";
    }
    #endregion


    #region Math Helpers
    private static Vector3 Fir(Vector3 from, Vector3 to, float k, float dt)
    {
        var exponent = dt * 120f; // reference frame rate, it's 2024, for fox sake!

        var alpha = MathF.Pow(k, exponent);

        return alpha * from + to * (1.0f - alpha);
    }
    #endregion

    #region Constants / Strings
    private const string InfoText =
        """
        DemoCubes (Stride version)

        All motion  is 100% CPU simulation (no GPU). Here, we demonstrate a simple case how to update the positions of a large number of Entities.

        State is stored in Components on the Entities:
          1x Stride.Core.Mathematics.Vector3 
             (as Position)
          1x Stride.Core.Mathematics.Matrix
             (as Transform)
          1x integer (as a simple identifier)

        The state is transferred into the Stride Engine in bulk each frame using Query.Raw in order to submit just the Matrix structs directly to a InstancingUserArray.

        This static buffer is then used by to display the Cubes.
        """;
    #endregion
}