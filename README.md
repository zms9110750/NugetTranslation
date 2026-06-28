# NugetTranslation

这个项目可以下载 NuGet 包，解析其中的 XML 文档注释，通过 OpenAI 兼容的大语言模型进行翻译，然后输出为翻译文件。


## 2 怎么用

### 2.1 找到本地的 NuGet 全局缓存位置

**第一步：打开 CMD**

按 `Win + R`，输入 `cmd`，回车

**第二步：查看 NuGet 全局缓存路径**

在 CMD 中输入：

```cmd
dotnet nuget locals global-packages -l
```

会输出类似：

```
global-packages: C:\Users\你的用户名\.nuget\packages\
```

这就是 NuGet 全局缓存目录。

---

### 2.2 复制项目并放置翻译结果

#### NuGet 包的目录结构

NuGet 包在全局缓存中的结构如下：


```
C:\Users\你的用户名\.nuget\packages\
└── 包名小写/
    └── 版本号/
        └── lib/
            └── 目标框架/
                ├── 包名.dll
                └── 包名.xml      ← 英文文档
```

**示例**：
```
C:\Users\你的用户名\.nuget\packages\autofac\9.1.0\lib\net10.0\Autofac.xml
```

#### 本项目的目录结构

```
NugetTranslation\   ← 你的项目路径
└── packages/                                 ← 翻译输出目录
    └── 包名小写/
        └── 版本号/
            └── lib/
                └── 目标框架/
                    └── zh-Hans/
                        └── 包名.xml         ← 翻译后的文件
```

#### 如何把翻译结果放到 NuGet 缓存

项目文件夹和 NuGet 全局缓存目录的结构从`packages`开始，是一样的。

你可以直接拖动整个`packages`文件夹和nuget的`packages`文件夹进行合并。

`zh-Hans`文件夹会随着VS的多语言选择生效。也就是说仅当VS的语言设置为简体中文时，才会加载`zh-Hans`文件夹中的翻译文件。

或者如果你从 GitHub 上复制了单独的文件，也可以直接覆盖掉对应的xml文件。

---

## 如何自己用

### 3.1 复制项目

1. 登录你的 GitHub 账号（如果没有账号需要先注册）
2. 点击右上角的 **Fork** 按钮（如果你不在github页面看到这个readme，那么你需要打开项目地址：`https://github.com/zms9110750/NugetTranslation`）
3. 对弹出的提示框无脑点击下一步。

Fork 完成后，你的仓库里就有了这个项目。

---

### 3.2 配置环境

#### 打开设置页面

进入你 Fork 后的仓库 → 点击 **Settings** → 点击左侧的 **Secrets and variables** → 选择 **Actions**

#### 配置变量（区分大小写）

在 **Variables** 选项卡中，点击 **Add repository variable**，添加以下两个变量：

| Variable 名称 | 说明 |
|---------------|------|
| `MODEL` | 模型名称，如 deepseek 使用 `deepseek-v4-flash` |
| `ENDPOINT` | API 地址（如 deepseek 用 `https://api.deepseek.com`） |

#### 配置密钥（机密）

在 **Secrets** 选项卡中，点击 **Add new secret**，添加以下密钥：

| Secret 名称 | 说明 |
|-------------|------|
| `APIKEY` | 大语言模型的 API Key |

> 这三个值从你的大语言模型提供商获取。

---

### 3.3 执行 Action

#### 打开 Actions 页面

进入你的仓库 → 点击上方的 **Actions** 标签

#### 如果没有看到 Action

1. 点击 **Actions** 页面
2. 如果是第一次使用，GitHub 会提示 "Workflows aren't available on this repository yet"
3. 点击绿色的 **"I understand my workflows, go ahead and enable them"** 按钮
4. 或者直接 Push 一次代码，Action 就会自动启用

#### 执行翻译任务

1. 在 Actions 页面，左侧找到 **Translate NuGet Package**（或类似名称）
2. 点击右侧的 **Run workflow** 按钮
3. 输入：
   - **packageId**：要翻译的包名，如 `Microsoft.Agents.AI.OpenAI`
   - **version**：版本号，如 `1.6.2`（`*`则翻译最新版）
4. 点击绿色的 **Run workflow** 按钮

#### 等待完成

Action 运行过程中，点击正在运行的任务可以查看实时日志。

运行完成后，翻译好的文件会自动提交到你的仓库的 `packages/` 目录下。

如果你使用vs或vs code，拉取代码。
或者直接在github上下载整个仓库。