const SIM_API = import.meta.env.VITE_SIM_API ?? 'http://localhost:8099';

function App() {
  return (
    <main>
      <h1>SimPanel</h1>
      <p>DropCopy Simulator — painel de controle.</p>
      <p>
        SimCore API: <code>{SIM_API}</code>
      </p>
    </main>
  );
}

export default App;
