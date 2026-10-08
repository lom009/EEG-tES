import { useState } from "react";
import { TiltedSurface } from "./TiltedSurface.jsx";
import "./LoginScreen.css";

export function LoginScreen({ onLogin }) {
  const [account, setAccount] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);
  const [error, setError] = useState("");

  function handleSubmit(event) {
    event.preventDefault();
    if (!account.trim() || !password) {
      setError("请输入账号和密码");
      return;
    }
    setError("");
    onLogin?.();
  }

  return (
    <main className="login-shell">
      <div className="login-platform-label" aria-hidden="true">
        <i />
        <span>EEG-tES BRAIN-COMPUTER INTERFACE<br /><small>RESEARCH PLATFORM</small></span>
      </div>

      <TiltedSurface className="login-card-stage" maxTilt={3}>
        <form className="login-card" onSubmit={handleSubmit} noValidate>
          <img className="login-logo" src="/assets/eeg-tes-logo.svg" alt="EEG-tES" />
          <h1>欢迎使用 EEG-tES 脑机实验系统</h1>

          <div className="login-fields">
            <label htmlFor="login-account">账号</label>
            <input
              id="login-account"
              autoComplete="username"
              value={account}
              onChange={(event) => {
                setAccount(event.target.value);
                setError("");
              }}
              placeholder="请输入手机号"
            />

            <label htmlFor="login-password">密码</label>
            <input
              id="login-password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => {
                setPassword(event.target.value);
                setError("");
              }}
              placeholder="请输入密码"
            />
          </div>

          <div className="login-options">
            <label>
              <input type="checkbox" checked={remember} onChange={(event) => setRemember(event.target.checked)} />
              <span>记住密码</span>
            </label>
            <button type="button">忘记密码？</button>
          </div>

          <p className="login-error" role="alert" aria-live="polite">{error}</p>
          <button className="login-submit" type="submit">登录</button>
        </form>
      </TiltedSurface>

      <footer className="login-footer">杭州南粟科技有限公司</footer>
    </main>
  );
}
