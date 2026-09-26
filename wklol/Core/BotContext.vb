Namespace Core

    ''' <summary>
    ''' What the engine hands every module on Initialize — the services modules share.
    '''
    ''' Empty on purpose. wk puts its action governor, pause gate and focus gate here; none of
    ''' them has a job in this bot (one module, no client window, and the Orb Walker's own delay is
    ''' the only pacing it wants), so none was brought across. The class stays because it is the
    ''' contract <see cref="IBotModule"/> is written against: a module ported from wk keeps its
    ''' shape, and a shared service added later has one place to live.
    ''' </summary>
    Public NotInheritable Class BotContext
    End Class

End Namespace
